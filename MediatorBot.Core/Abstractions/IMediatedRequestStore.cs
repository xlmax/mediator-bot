namespace MediatorBot.Core;

public interface IMediatedRequestStore
{
    Task CreateAsync(
        MediatedRequest request,
        CancellationToken cancellationToken = default);

    Task MarkAwaitingResponseAsync(
        Guid sessionId,
        Guid requestId,
        CancellationToken cancellationToken = default);

    Task ResolveAsync(
        Guid sessionId,
        Guid requestId,
        MediatedRequestStatus status,
        DateTimeOffset resolvedAt,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MediatedRequest>> GetOpenAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);
}
