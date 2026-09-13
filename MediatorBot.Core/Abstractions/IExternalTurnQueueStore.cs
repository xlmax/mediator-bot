namespace MediatorBot.Core;

public interface IExternalTurnQueueStore
{
    Task<bool> TryEnqueueAsync(
        PendingExternalTurn turn,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PendingExternalTurn>> GetPendingAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);

    Task<int> BeginAttemptAsync(
        Guid sessionId,
        Guid turnId,
        DateTimeOffset attemptedAt,
        CancellationToken cancellationToken = default);

    Task MarkFailedAsync(
        Guid sessionId,
        Guid turnId,
        string failureType,
        DateTimeOffset failedAt,
        CancellationToken cancellationToken = default);

    Task<int> GetFailedCountAsync(
        Guid sessionId,
        Guid participantId,
        CancellationToken cancellationToken = default);

    Task<int> RetryFailedAsync(
        Guid sessionId,
        Guid participantId,
        CancellationToken cancellationToken = default);

    Task CompleteAsync(
        Guid sessionId,
        Guid turnId,
        CancellationToken cancellationToken = default);
}
