namespace MediatorBot.Core;

public interface ISessionTurnCoordinator
{
    Task<TResult> ExecuteAsync<TResult>(
        Guid sessionId,
        Func<CancellationToken, Task<TResult>> turn,
        CancellationToken cancellationToken = default);
}
