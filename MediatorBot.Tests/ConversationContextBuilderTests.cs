using MediatorBot.Core;
using MediatorBot.Infrastructure;

namespace MediatorBot.Tests;

public sealed class ConversationContextBuilderTests
{
    [Fact]
    public async Task BuildAsync_UsesConfiguredNumberOfLatestSharedMessages()
    {
        var participantA = new Participant(Guid.NewGuid(), "A");
        var participantB = new Participant(Guid.NewGuid(), "B");
        var session = new Session(Guid.NewGuid(), participantA, participantB);
        var store = new InMemoryConversationStore([session]);
        var start = DateTimeOffset.UtcNow;

        var first = Incoming(session, participantA, "Первое", start);
        var mediator = Outgoing(session, participantA, "Ответ", start.AddSeconds(1));
        var current = Incoming(session, participantB, "Текущее", start.AddSeconds(2));
        await store.SaveMessageAsync(first);
        await store.SaveMessageAsync(mediator);
        await store.SaveMessageAsync(current);

        var builder = new ConversationContextBuilder(store, 2);
        var context = await builder.BuildAsync(session, current);

        Assert.Equal(participantA, context.ParticipantA);
        Assert.Equal(participantB, context.ParticipantB);
        Assert.Equal(participantB, context.Author);
        Assert.Equal(current, context.IncomingMessage);
        Assert.Equal([mediator.Id, current.Id], context.History.Select(message => message.Id));
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
}
