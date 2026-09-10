namespace MediatorBot.Core;

public sealed class MediatorDeliveryRecorder(IConversationStore conversationStore)
    : IMediatorDeliveryRecorder
{
    public async Task RecordDeliveredAsync(
        Guid sessionId,
        Guid participantId,
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var session = await conversationStore.GetSessionAsync(sessionId, cancellationToken)
            ?? throw new KeyNotFoundException($"Session '{sessionId}' was not found.");
        var recipient = session.GetParticipant(participantId);
        var message = new Message(
            Guid.NewGuid(),
            session.Id,
            null,
            recipient.Id,
            MessageDirection.MediatorToParticipant,
            text,
            DateTimeOffset.UtcNow);

        await conversationStore.SaveMessageAsync(message, cancellationToken);
    }
}
