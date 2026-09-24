namespace MediatorBot.Core;

public enum RelationshipPhase
{
    Calm,
    Tension,
    ActiveConflict,
    CoolingDown,
    RepairWindow,
    Uncertain
}

public enum InitiativeConfidence
{
    Low,
    Medium,
    High
}

public enum InitiativeDecisionKind
{
    NoAction,
    ReevaluateLater,
    ContactParticipant,
    ContactBoth
}

public enum InitiativeIntent
{
    Observe,
    CheckIn,
    AssessReadiness,
    SupportRepair,
    Bridge,
    ConfirmPositiveState
}

public enum InitiativeReasonCode
{
    NoUsefulAction,
    RecentConflict,
    RequestedSpace,
    RecentInitiative,
    RepairOpportunity,
    PositiveStateUncertain,
    ParticipantPreference,
    SafetyRisk,
    Other
}

public enum InitiativeDecisionStatus
{
    NoDelivery,
    Shadow,
    PendingDelivery,
    Delivered,
    Superseded,
    Suppressed,
    Failed
}

public sealed record InitiativeProposal(
    RelationshipPhase Phase,
    InitiativeConfidence Confidence,
    InitiativeDecisionKind DecisionKind,
    Guid? TargetParticipantId,
    InitiativeIntent Intent,
    InitiativeReasonCode ReasonCode,
    string OperationalRationale,
    string? TextForParticipantA,
    string? TextForParticipantB,
    int ReevaluateAfterMinutes,
    Guid? PauseParticipantId = null,
    int? PauseForMinutes = null);

public sealed record InitiativeDecision(
    Guid Id,
    Guid SessionId,
    Guid ObservedParticipantMessageId,
    DateTimeOffset EvaluatedAt,
    RelationshipPhase Phase,
    InitiativeConfidence Confidence,
    InitiativeDecisionKind DecisionKind,
    Guid? TargetParticipantId,
    InitiativeIntent Intent,
    InitiativeReasonCode ReasonCode,
    string OperationalRationale,
    string? TextForParticipantA,
    string? TextForParticipantB,
    DateTimeOffset NextEvaluationAt,
    InitiativeDecisionStatus Status,
    int AttemptCount = 0,
    DateTimeOffset? DeliveredAt = null,
    DateTimeOffset? FailedAt = null,
    string? FailureType = null,
    Guid? PauseParticipantId = null,
    DateTimeOffset? PauseUntil = null);

public sealed record InitiativeParticipantPreference(
    Guid SessionId,
    Guid ParticipantId,
    bool IsEnabled,
    DateTimeOffset? PauseUntil,
    DateTimeOffset UpdatedAt)
{
    public bool AllowsContact(DateTimeOffset now) =>
        IsEnabled && (PauseUntil is null || PauseUntil <= now);
}
