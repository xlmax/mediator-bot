namespace MediatorBot.Core;

public interface IConversationCompactionStore
{
    Task<ConversationSummary?> GetSummaryAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);

    Task<ConversationCompactionBatch?> GetCompactionBatchAsync(
        Guid sessionId,
        int triggerMessageCount,
        int triggerCharacterCount,
        int retainRecentMessageCount,
        int retainRecentCharacterCount,
        CancellationToken cancellationToken = default);

    Task CommitCompactionAsync(
        Guid sessionId,
        long expectedSummaryVersion,
        long compactedThroughSequence,
        ConversationSummaryContent summary,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);
}
