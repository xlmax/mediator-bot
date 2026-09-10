namespace MediatorBot.Core;

public sealed class MediationService(
    IConversationStore conversationStore,
    IModelRuntime modelRuntime)
{
    public async Task<IReadOnlyList<MediatorAction>> HandleMessageAsync(
        Guid sessionId,
        Guid participantId,
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var session = await conversationStore.GetSessionAsync(sessionId, cancellationToken)
            ?? throw new KeyNotFoundException($"Session '{sessionId}' was not found.");
        var author = session.GetParticipant(participantId);

        var incomingMessage = new Message(
            Guid.NewGuid(),
            session.Id,
            author.Id,
            text,
            DateTimeOffset.UtcNow);

        await conversationStore.SaveMessageAsync(incomingMessage, cancellationToken);
        var history = await conversationStore.GetHistoryAsync(session.Id, cancellationToken);

        var context = new ConversationContext(
            session,
            author,
            history,
            incomingMessage);

        var result = await modelRuntime.ProcessAsync(context, cancellationToken);
        return result.Actions;
    }
}
