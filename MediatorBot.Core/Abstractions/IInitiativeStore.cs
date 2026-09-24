namespace MediatorBot.Core;

public interface IInitiativeStore
{
    Task<Guid?> GetLatestParticipantMessageIdAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);

    Task<InitiativeDecision?> GetLatestDecisionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<InitiativeDecision>> GetRecentDecisionsAsync(
        Guid sessionId,
        int limit,
        CancellationToken cancellationToken = default);

    Task<bool> TrySaveDecisionAsync(
        InitiativeDecision decision,
        Guid? expectedLatestDecisionId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<InitiativeDecision>> GetPendingDeliveryDecisionsAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);

    Task<int> BeginDecisionAttemptAsync(
        Guid sessionId,
        Guid decisionId,
        CancellationToken cancellationToken = default);

    Task MarkDecisionStatusAsync(
        Guid sessionId,
        Guid decisionId,
        InitiativeDecisionStatus status,
        DateTimeOffset changedAt,
        string? failureType = null,
        CancellationToken cancellationToken = default);

    Task<int> CountDeliveredContactsAsync(
        Guid sessionId,
        Guid participantId,
        DateTimeOffset since,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<InitiativeParticipantPreference>> GetParticipantPreferencesAsync(
        Session session,
        CancellationToken cancellationToken = default);

    Task SetParticipantEnabledAsync(
        Guid sessionId,
        Guid participantId,
        bool enabled,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken = default);

    Task PauseParticipantAsync(
        Guid sessionId,
        Guid participantId,
        DateTimeOffset pauseUntil,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken = default);
}
