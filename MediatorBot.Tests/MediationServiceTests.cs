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
        var service = CreateService(store, runtime);

        await service.HandleMessageAsync(session.Id, participantA.Id, "Привет");

        var message = Assert.Single(await store.GetHistoryAsync(session.Id));
        Assert.Equal(session.Id, message.SessionId);
        Assert.Equal(participantA.Id, message.AuthorId);
        Assert.Null(message.RecipientId);
        Assert.Equal(MessageDirection.ParticipantToMediator, message.Direction);
        Assert.Equal("Привет", message.Text);
    }

    [Fact]
    public async Task HandleMessageAsync_GivesModelSharedHistoryIncludingEarlierMediatorMessages()
    {
        var (session, participantA, participantB) = CreateSession();
        var store = new InMemoryConversationStore([session]);
        var runtime = new RecordingModelRuntime(context => new ModelResult(
            [new SendToParticipant(context.Author.Id, $"Ответ для {context.Author.DisplayName}")]));
        var service = CreateService(store, runtime);

        await service.HandleMessageAsync(session.Id, participantA.Id, "Сообщение A");
        await service.HandleMessageAsync(session.Id, participantB.Id, "Сообщение B");

        var context = runtime.Contexts[1];
        Assert.Same(session, context.Session);
        Assert.Equal(participantA, context.ParticipantA);
        Assert.Equal(participantB, context.ParticipantB);
        Assert.Equal(participantB, context.Author);
        Assert.Collection(
            context.History,
            message => Assert.Equal(MessageDirection.ParticipantToMediator, message.Direction),
            message =>
            {
                Assert.Equal(MessageDirection.MediatorToParticipant, message.Direction);
                Assert.Equal(participantA.Id, message.RecipientId);
            },
            message => Assert.Equal(participantB.Id, message.AuthorId));
    }

    [Fact]
    public async Task HandleMessageAsync_DoesNotMixDifferentSessions()
    {
        var (firstSession, firstParticipant, _) = CreateSession();
        var (secondSession, secondParticipant, _) = CreateSession();
        var store = new InMemoryConversationStore([firstSession, secondSession]);
        var runtime = new RecordingModelRuntime();
        var service = CreateService(store, runtime);

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
    public async Task FakeModelRuntime_AddressesActionAndSavedMessageToCurrentAuthor()
    {
        var (session, _, participantB) = CreateSession();
        var store = new InMemoryConversationStore([session]);
        var service = CreateService(store, new FakeModelRuntime());

        var actions = await service.HandleMessageAsync(
            session.Id,
            participantB.Id,
            "У меня другая версия");

        var action = Assert.IsType<SendToParticipant>(Assert.Single(actions));
        Assert.Equal(participantB.Id, action.ParticipantId);
        Assert.Equal(
            "Получил сообщение от B: У меня другая версия",
            action.Text);

        var history = await store.GetHistoryAsync(session.Id);
        Assert.Equal(2, history.Count);
        Assert.Equal(MessageDirection.MediatorToParticipant, history[1].Direction);
        Assert.Null(history[1].AuthorId);
        Assert.Equal(participantB.Id, history[1].RecipientId);
        Assert.Equal(action.Text, history[1].Text);
    }

    [Fact]
    public async Task SendToBoth_IsSavedAsTwoAddressedMessages()
    {
        var (session, participantA, participantB) = CreateSession();
        var store = new InMemoryConversationStore([session]);
        var runtime = new RecordingModelRuntime(_ => new ModelResult(
            [new SendToBoth("Для A", "Для B")]));
        var service = CreateService(store, runtime);

        await service.HandleMessageAsync(session.Id, participantA.Id, "Вопрос");

        var history = await store.GetHistoryAsync(session.Id);
        Assert.Collection(
            history,
            incoming => Assert.Equal(MessageDirection.ParticipantToMediator, incoming.Direction),
            toA => Assert.Equal(participantA.Id, toA.RecipientId),
            toB => Assert.Equal(participantB.Id, toB.RecipientId));
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

    private static MediationService CreateService(
        IConversationStore store,
        IModelRuntime runtime) => new(
            store,
            runtime,
            new ConversationContextBuilder(store, 100));

    private static (Session Session, Participant ParticipantA, Participant ParticipantB) CreateSession()
    {
        var participantA = new Participant(Guid.NewGuid(), "A");
        var participantB = new Participant(Guid.NewGuid(), "B");
        var session = new Session(Guid.NewGuid(), participantA, participantB);
        return (session, participantA, participantB);
    }

    private sealed class RecordingModelRuntime(
        Func<ConversationContext, ModelResult>? handler = null) : IModelRuntime
    {
        public List<ConversationContext> Contexts { get; } = [];

        public Task<ModelResult> ProcessAsync(
            ConversationContext context,
            CancellationToken cancellationToken = default)
        {
            Contexts.Add(context);
            return Task.FromResult(handler?.Invoke(context) ?? new ModelResult([]));
        }
    }
}
