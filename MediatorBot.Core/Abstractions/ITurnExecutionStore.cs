namespace MediatorBot.Core;

public interface ITurnExecutionStore
{
    Task<string?> GetModelResultAsync(
        Guid sessionId,
        Guid turnId,
        CancellationToken cancellationToken = default);

    Task SaveModelResultAsync(
        Guid sessionId,
        Guid turnId,
        string modelResultJson,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TurnDelivery>> EnsureDeliveryPlanAsync(
        TurnDeliveryPlan plan,
        CancellationToken cancellationToken = default);

    Task MarkDeliveryAttemptingAsync(
        Guid sessionId,
        Guid turnId,
        Guid deliveryId,
        DateTimeOffset attemptedAt,
        CancellationToken cancellationToken = default);

    Task RecordDeliveryAsync(
        Guid sessionId,
        Guid turnId,
        Guid deliveryId,
        DateTimeOffset deliveredAt,
        CancellationToken cancellationToken = default);
}
