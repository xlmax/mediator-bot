using MediatorBot.Core;
using MediatorBot.Infrastructure;

namespace MediatorBot.Tests;

public sealed class MediationServiceTests
{
    [Fact]
    public async Task HandleMessageAsync_SavesIncomingMessageWithCorrectAuthor()
    {
        var (session, participantA, _) = CreateSession();
        var store = new InMemoryConversationStore([session]);
        var runtime = new RecordingModelRuntime();
        var service = new MediationService(store, runtime);

        await service.HandleMessageAsync(session.Id, participantA.Id, "Привет");

        var message = Assert.Single(await store.GetHistoryAsync(session.Id));
        Assert.Equal(session.Id, message.SessionId);
        Assert.Equal(participantA.Id, message.AuthorId);
        Assert.Equal("Привет", message.Text);
    }

    [Fact]
    public async Task HandleMessageAsync_GivesModelSessionParticipantsAndHistoryFromBothAuthors()
    {
        var (session, participantA, participantB) = CreateSession();
        var store = new InMemoryConversationStore([session]);
        var runtime = new RecordingModelRuntime();
        var service = new MediationService(store, runtime);

        await service.HandleMessageAsync(session.Id, participantA.Id, "Сообщение A");
        await service.HandleMessageAsync(session.Id, participantB.Id, "Сообщение B");

        var context = runtime.Contexts[1];
        Assert.Same(session, context.Session);
        Assert.Equal(participantA, context.ParticipantA);
        Assert.Equal(participantB, context.ParticipantB);
        Assert.Equal(participantB, context.Author);
        Assert.Equal(participantB.Id, context.IncomingMessage.AuthorId);
        Assert.Collection(
            context.History,
            message => Assert.Equal(participantA.Id, message.AuthorId),
            message => Assert.Equal(participantB.Id, message.AuthorId));
    }

    [Fact]
    public async Task HandleMessageAsync_DoesNotMixDifferentSessions()
    {
        var (firstSession, firstParticipant, _) = CreateSession();
        var (secondSession, secondParticipant, _) = CreateSession();
        var store = new InMemoryConversationStore([firstSession, secondSession]);
        var runtime = new RecordingModelRuntime();
        var service = new MediationService(store, runtime);

        await service.HandleMessageAsync(firstSession.Id, firstParticipant.Id, "Первая сессия");
        await service.HandleMessageAsync(secondSession.Id, secondParticipant.Id, "Вторая сессия");

        Assert.All(runtime.Contexts[0].History, message =>
            Assert.Equal(firstSession.Id, message.SessionId));
        Assert.All(runtime.Contexts[1].History, message =>
            Assert.Equal(secondSession.Id, message.SessionId));
        Assert.Single(runtime.Contexts[0].History);
        Assert.Single(runtime.Contexts[1].History);
    }

    [Fact]
    public async Task FakeModelRuntime_AddressesActionToCurrentAuthor()
    {
        var (session, _, participantB) = CreateSession();
        var store = new InMemoryConversationStore([session]);
        var service = new MediationService(store, new FakeModelRuntime());

        var actions = await service.HandleMessageAsync(
            session.Id,
            participantB.Id,
            "У меня другая версия");

        var action = Assert.IsType<SendToParticipant>(Assert.Single(actions));
        Assert.Equal(participantB.Id, action.ParticipantId);
        Assert.Equal(
            "Получил сообщение от B: У меня другая версия",
            action.Text);
    }

    [Fact]
    public void Core_DoesNotReferenceConsoleUi()
    {
        var referencedAssemblies = typeof(MediationService)
            .Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name);

        Assert.DoesNotContain("MediatorBot.Console", referencedAssemblies);
    }

    private static (Session Session, Participant ParticipantA, Participant ParticipantB) CreateSession()
    {
        var participantA = new Participant(Guid.NewGuid(), "A");
        var participantB = new Participant(Guid.NewGuid(), "B");
        var session = new Session(Guid.NewGuid(), participantA, participantB);
        return (session, participantA, participantB);
    }

    private sealed class RecordingModelRuntime : IModelRuntime
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
}
