namespace MediatorBot.Core;

public interface IInitiativeContextBuilder
{
    Task<InitiativeContext?> BuildAsync(
        Guid sessionId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}
