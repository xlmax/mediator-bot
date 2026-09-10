using System.Text;
using MediatorBot.Core;
using MediatorBot.Infrastructure;
using Microsoft.Data.Sqlite;

namespace MediatorBot.Tests;

public sealed class SqliteConversationStoreTests
{
    [Fact]
    public async Task Store_CreatesSessionAndRestoresParticipantsAndOrderedHistory()
    {
        using var database = new TemporaryDatabase();
        var firstStore = database.CreateStore();
        var (session, participantA, participantB) = CreateSession();
        await firstStore.CreateSessionAsync(session);

        var start = DateTimeOffset.UtcNow;
        var messages = new[]
        {
            Incoming(session, participantA, "A -> mediator", start),
            Outgoing(session, participantA, "mediator -> A", start.AddMilliseconds(1)),
            Incoming(session, participantB, "B -> mediator", start.AddMilliseconds(2)),
            Outgoing(session, participantB, "mediator -> B", start.AddMilliseconds(3))
        };

        foreach (var message in messages)
        {
            await firstStore.SaveMessageAsync(message);
        }

        var restartedStore = database.CreateStore();
        var restoredSession = await restartedStore.GetSessionAsync(session.Id);
        var restoredHistory = await restartedStore.GetHistoryAsync(session.Id);

        Assert.NotNull(restoredSession);
        Assert.Equal(session.Id, restoredSession.Id);
        Assert.Equal(session.CreatedAt, restoredSession.CreatedAt);
        Assert.Equal(participantA, restoredSession.ParticipantA);
        Assert.Equal(participantB, restoredSession.ParticipantB);
        Assert.Equal(messages.Select(message => message.Id), restoredHistory.Select(message => message.Id));
        Assert.Equal(
            [
                MessageDirection.ParticipantToMediator,
                MessageDirection.MediatorToParticipant,
                MessageDirection.ParticipantToMediator,
                MessageDirection.MediatorToParticipant
            ],
            restoredHistory.Select(message => message.Direction));
    }

    [Fact]
    public async Task Store_DoesNotMixHistoriesOfDifferentSessions()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore();
        var (firstSession, firstParticipant, _) = CreateSession();
        var (secondSession, secondParticipant, _) = CreateSession();
        await store.CreateSessionAsync(firstSession);
        await store.CreateSessionAsync(secondSession);

        await store.SaveMessageAsync(Incoming(
            firstSession,
            firstParticipant,
            "Первая",
            DateTimeOffset.UtcNow));
        await store.SaveMessageAsync(Incoming(
            secondSession,
            secondParticipant,
            "Вторая",
            DateTimeOffset.UtcNow));

        var firstHistory = await store.GetHistoryAsync(firstSession.Id);
        var secondHistory = await store.GetHistoryAsync(secondSession.Id);

        Assert.Equal("Первая", Assert.Single(firstHistory).Text);
        Assert.Equal("Вторая", Assert.Single(secondHistory).Text);
    }

    [Fact]
    public async Task RestartedStore_GivesPreviousIncomingAndOutgoingHistoryToModel()
    {
        using var database = new TemporaryDatabase();
        var (session, participantA, participantB) = CreateSession();
        var firstStore = database.CreateStore();
        await firstStore.CreateSessionAsync(session);
        var firstService = new MediationService(
            firstStore,
            new FakeModelRuntime(),
            new ConversationContextBuilder(firstStore, 100));

        var firstActions = await firstService.HandleMessageAsync(
            session.Id,
            participantA.Id,
            "До перезапуска");
        var firstReply = Assert.IsType<SendToParticipant>(Assert.Single(firstActions));
        await new MediatorDeliveryRecorder(firstStore).RecordDeliveredAsync(
            session.Id,
            firstReply.ParticipantId,
            firstReply.Text);

        var restartedStore = database.CreateStore();
        var runtime = new CapturingModelRuntime();
        var restartedService = new MediationService(
            restartedStore,
            runtime,
            new ConversationContextBuilder(restartedStore, 100));
        await restartedService.HandleMessageAsync(
            session.Id,
            participantB.Id,
            "После перезапуска");

        var context = Assert.Single(runtime.Contexts);
        Assert.Collection(
            context.History,
            message => Assert.Equal("До перезапуска", message.Text),
            message =>
            {
                Assert.Equal(MessageDirection.MediatorToParticipant, message.Direction);
                Assert.Equal(participantA.Id, message.RecipientId);
            },
            message => Assert.Equal("После перезапуска", message.Text));
    }

    [Fact]
    public async Task DatabaseFile_IsEncryptedAndCannotBeOpenedWithoutKey()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore();
        var (session, participantA, _) = CreateSession();
        await store.CreateSessionAsync(session);
        await store.SaveMessageAsync(Incoming(
            session,
            participantA,
            "Очень секретный текст",
            DateTimeOffset.UtcNow));

        SqliteConnection.ClearAllPools();
        var bytes = await File.ReadAllBytesAsync(database.Path);
        var header = Encoding.ASCII.GetString(bytes, 0, Math.Min(16, bytes.Length));
        Assert.NotEqual("SQLite format 3\0", header);
        Assert.DoesNotContain("Очень секретный текст", Encoding.UTF8.GetString(bytes));

        var storeWithWrongKey = new SqliteConversationStore(
            new SqliteConversationStoreOptions(database.Path, "wrong-key"));
        await Assert.ThrowsAsync<SqliteException>(() => storeWithWrongKey.InitializeAsync());
    }

    private static (Session Session, Participant ParticipantA, Participant ParticipantB) CreateSession()
    {
        var participantA = new Participant(Guid.NewGuid(), "A");
        var participantB = new Participant(Guid.NewGuid(), "B");
        return (
            new Session(Guid.NewGuid(), participantA, participantB),
            participantA,
            participantB);
    }

    private static Message Incoming(
        Session session,
        Participant author,
        string text,
        DateTimeOffset createdAt) => new(
            Guid.NewGuid(),
            session.Id,
            author.Id,
            null,
            MessageDirection.ParticipantToMediator,
            text,
            createdAt);

    private static Message Outgoing(
        Session session,
        Participant recipient,
        string text,
        DateTimeOffset createdAt) => new(
            Guid.NewGuid(),
            session.Id,
            null,
            recipient.Id,
            MessageDirection.MediatorToParticipant,
            text,
            createdAt);

    private sealed class CapturingModelRuntime : IModelRuntime
    {
        public List<ConversationContext> Contexts { get; } = [];

        public Task<ModelResult> ProcessAsync(
            ConversationContext context,
            CancellationToken cancellationToken = default)
        {
            Contexts.Add(context);
            return Task.FromResult(new ModelResult([]));
        }
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "MediatorBot.Tests",
            Guid.NewGuid().ToString("N"));

        public TemporaryDatabase()
        {
            Directory.CreateDirectory(_directory);
            Path = System.IO.Path.Combine(_directory, "mediator-bot.db");
        }

        public string Path { get; }

        public SqliteConversationStore CreateStore() => new(
            new SqliteConversationStoreOptions(Path, "integration-test-key"));

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
