namespace MediatorBot.Core;

public sealed class ConversationContextBuilder : IConversationContextBuilder
{
    private readonly IConversationStore _conversationStore;
    private readonly int _maxHistoryMessages;

    public ConversationContextBuilder(
        IConversationStore conversationStore,
        int maxHistoryMessages)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxHistoryMessages);
        _conversationStore = conversationStore;
        _maxHistoryMessages = maxHistoryMessages;
    }

    public async Task<ConversationContext> BuildAsync(
        Session session,
        Message incomingMessage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(incomingMessage);

        if (incomingMessage.SessionId != session.Id ||
            incomingMessage.Direction != MessageDirection.ParticipantToMediator ||
            incomingMessage.AuthorId is null)
        {
            throw new ArgumentException(
                "The incoming message must belong to the supplied session.",
                nameof(incomingMessage));
        }

        var author = session.GetParticipant(incomingMessage.AuthorId.Value);
        var history = await _conversationStore.GetHistoryAsync(
            session.Id,
            _maxHistoryMessages,
            cancellationToken);

        return new ConversationContext(
            session,
            author,
            history,
            incomingMessage);
    }
}
