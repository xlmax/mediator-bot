namespace MediatorBot.Core;

public sealed class ConversationContextBuilder : IConversationContextBuilder
{
    private readonly IConversationStore _conversationStore;
    private readonly IMediatedRequestStore _mediatedRequestStore;
    private readonly int _maxHistoryMessages;

    public ConversationContextBuilder(
        IConversationStore conversationStore,
        IMediatedRequestStore mediatedRequestStore,
        int maxHistoryMessages)
    {
        ArgumentNullException.ThrowIfNull(conversationStore);
        ArgumentNullException.ThrowIfNull(mediatedRequestStore);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxHistoryMessages);
        _conversationStore = conversationStore;
        _mediatedRequestStore = mediatedRequestStore;
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
        var historyTask = _conversationStore.GetHistoryAsync(
            session.Id,
            _maxHistoryMessages,
            cancellationToken);
        var openRequestsTask = _mediatedRequestStore.GetOpenAsync(
            session.Id,
            cancellationToken);
        await Task.WhenAll(historyTask, openRequestsTask);

        return new ConversationContext(
            session,
            author,
            await historyTask,
            incomingMessage)
        {
            OpenMediatedRequests = await openRequestsTask
        };
    }
}
