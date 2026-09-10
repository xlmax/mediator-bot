using System.Collections.Concurrent;

namespace MediatorBot.Core;

public sealed class SessionTurnCoordinator : ISessionTurnCoordinator
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _sessionLocks = new();

    public async Task<TResult> ExecuteAsync<TResult>(
        Guid sessionId,
        Func<CancellationToken, Task<TResult>> turn,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(turn);

        var sessionLock = _sessionLocks.GetOrAdd(
            sessionId,
            static _ => new SemaphoreSlim(1, 1));
        await sessionLock.WaitAsync(cancellationToken);
        try
        {
            return await turn(cancellationToken);
        }
        finally
        {
            sessionLock.Release();
        }
    }
}
