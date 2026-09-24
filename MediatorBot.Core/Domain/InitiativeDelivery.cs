namespace MediatorBot.Core;

public enum InitiativeDeliveryStatus
{
    Pending,
    Attempting,
    Delivered
}

public sealed record InitiativeDelivery(
    Guid Id,
    Guid DecisionId,
    Guid SessionId,
    Guid ParticipantId,
    Guid LogicalMessageId,
    string DeliveryKey,
    int ChunkIndex,
    int ChunkCount,
    string Text,
    InitiativeDeliveryStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? AttemptedAt = null,
    DateTimeOffset? DeliveredAt = null);

public sealed record InitiativeDeliveryPlan(
    Guid DecisionId,
    Guid SessionId,
    Guid ParticipantId,
    string DeliveryKey,
    IReadOnlyList<string> Chunks,
    DateTimeOffset CreatedAt);
