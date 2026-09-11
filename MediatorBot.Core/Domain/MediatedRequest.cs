namespace MediatorBot.Core;

public enum MediatedRequestStatus
{
    PendingDelivery,
    AwaitingResponse,
    Answered,
    Declined,
    NoShareableAnswer,
    Cancelled
}

public sealed record MediatedRequest(
    Guid Id,
    Guid SessionId,
    Guid RequesterId,
    Guid RespondentId,
    string Summary,
    MediatedRequestStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ResolvedAt = null)
{
    public bool IsOpen => Status is
        MediatedRequestStatus.PendingDelivery or
        MediatedRequestStatus.AwaitingResponse;
}
