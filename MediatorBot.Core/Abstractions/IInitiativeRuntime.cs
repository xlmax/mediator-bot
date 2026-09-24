namespace MediatorBot.Core;

public interface IInitiativeRuntime
{
    Task<InitiativeProposal> EvaluateAsync(
        InitiativeContext context,
        CancellationToken cancellationToken = default);
}
