namespace MediatorBot.Core;

public enum TurnDeliveryStatus
{
    Pending,
    Attempting,
    Delivered
}

public sealed record TurnDelivery(
    Guid Id,
    Guid TurnId,
    Guid SessionId,
    Guid ParticipantId,
    Guid LogicalMessageId,
    string DeliveryKey,
    int ChunkIndex,
    int ChunkCount,
    string Text,
    string ActionType,
    DisclosureDecision DisclosureDecision,
    TurnDeliveryStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? AttemptedAt = null,
    DateTimeOffset? DeliveredAt = null);

public sealed record TurnDeliveryPlan(
    Guid TurnId,
    Guid SessionId,
    Guid ParticipantId,
    string DeliveryKey,
    IReadOnlyList<string> Chunks,
    string ActionType,
    DisclosureDecision DisclosureDecision,
    DateTimeOffset CreatedAt);
