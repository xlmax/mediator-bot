using MediatorBot.Core;

namespace MediatorBot.Infrastructure;

public sealed class DisabledInitiativeRuntime : IInitiativeRuntime
{
    public Task<InitiativeProposal> EvaluateAsync(
        InitiativeContext context,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new InitiativeProposal(
            RelationshipPhase.Uncertain,
            InitiativeConfidence.Low,
            InitiativeDecisionKind.NoAction,
            null,
            InitiativeIntent.Observe,
            InitiativeReasonCode.NoUsefulAction,
            "Проактивная оценка недоступна для выбранного model runtime.",
            null,
            null,
            10_080));
}
