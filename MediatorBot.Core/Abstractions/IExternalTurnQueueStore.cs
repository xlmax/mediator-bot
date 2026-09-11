namespace MediatorBot.Core;

public interface IExternalTurnQueueStore
{
    Task<bool> TryEnqueueAsync(
        PendingExternalTurn turn,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PendingExternalTurn>> GetPendingAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);

    Task CompleteAsync(
        Guid sessionId,
        Guid turnId,
        CancellationToken cancellationToken = default);
}
