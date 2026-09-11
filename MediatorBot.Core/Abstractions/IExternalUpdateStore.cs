namespace MediatorBot.Core;

public interface IExternalUpdateStore
{
    Task<bool> TryRegisterAsync(
        string source,
        Guid sessionId,
        string externalUpdateId,
        CancellationToken cancellationToken = default);
}
