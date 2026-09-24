namespace MediatorBot.Core;

public interface IInitiativeDeliveryStore
{
    Task<bool> HasDeliveredInitiativeMessageAsync(
        Guid sessionId,
        Guid decisionId,
        Guid participantId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<InitiativeDelivery>> EnsureInitiativeDeliveryPlanAsync(
        InitiativeDeliveryPlan plan,
        CancellationToken cancellationToken = default);

    Task MarkInitiativeDeliveryAttemptingAsync(
        Guid sessionId,
        Guid decisionId,
        Guid deliveryId,
        DateTimeOffset attemptedAt,
        CancellationToken cancellationToken = default);

    Task RecordInitiativeDeliveryAsync(
        Guid sessionId,
        Guid decisionId,
        Guid deliveryId,
        DateTimeOffset deliveredAt,
        CancellationToken cancellationToken = default);
}
