namespace MediatorBot.Core;

public interface IConversationCompactionService
{
    Task<ConversationCompactionPlan?> PrepareAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);

    Task<ConversationCompactionResult> ExecuteAsync(
        ConversationCompactionPlan plan,
        CancellationToken cancellationToken = default);
}
