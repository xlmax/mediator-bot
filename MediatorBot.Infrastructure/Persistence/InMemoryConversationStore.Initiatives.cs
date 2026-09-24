using MediatorBot.Core;

namespace MediatorBot.Infrastructure;

public sealed partial class InMemoryConversationStore
{
    private readonly Dictionary<Guid, List<InitiativeDecision>> _initiativeDecisions = [];
    private readonly Dictionary<(Guid SessionId, Guid ParticipantId),
        InitiativeParticipantPreference> _initiativePreferences = [];
    private readonly Dictionary<Guid, InitiativeDelivery> _initiativeDeliveries = [];

    public Task<Guid?> GetLatestParticipantMessageIdAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            var id = _messagesBySession[sessionId]
                .LastOrDefault(item =>
                    item.Message.Direction == MessageDirection.ParticipantToMediator)
                ?.Message.Id;
            return Task.FromResult(id);
        }
    }

    public Task<InitiativeDecision?> GetLatestDecisionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            _initiativeDecisions.TryGetValue(sessionId, out var decisions);
            return Task.FromResult(decisions?.LastOrDefault());
        }
    }

    public Task<IReadOnlyList<InitiativeDecision>> GetRecentDecisionsAsync(
        Guid sessionId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            _initiativeDecisions.TryGetValue(sessionId, out var decisions);
            return Task.FromResult<IReadOnlyList<InitiativeDecision>>(
                decisions?.TakeLast(limit).ToArray() ?? []);
        }
    }

    public Task<bool> TrySaveDecisionAsync(
        InitiativeDecision decision,
        Guid? expectedLatestDecisionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decision);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            if (!_initiativeDecisions.TryGetValue(decision.SessionId, out var decisions))
            {
                decisions = [];
                _initiativeDecisions.Add(decision.SessionId, decisions);
            }

            if (decisions.LastOrDefault()?.Id != expectedLatestDecisionId)
            {
                return Task.FromResult(false);
            }

            decisions.Add(decision);
            if (decision.PauseParticipantId is Guid pauseParticipantId &&
                decision.PauseUntil is DateTimeOffset pauseUntil)
            {
                var existing = _initiativePreferences.GetValueOrDefault(
                    (decision.SessionId, pauseParticipantId));
                _initiativePreferences[(decision.SessionId, pauseParticipantId)] = new(
                    decision.SessionId,
                    pauseParticipantId,
                    existing?.IsEnabled ?? true,
                    existing?.PauseUntil is DateTimeOffset current && current > pauseUntil
                        ? current
                        : pauseUntil,
                    decision.EvaluatedAt);
            }

            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<InitiativeDecision>> GetPendingDeliveryDecisionsAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            _initiativeDecisions.TryGetValue(sessionId, out var decisions);
            return Task.FromResult<IReadOnlyList<InitiativeDecision>>(
                decisions?.Where(item =>
                    item.Status == InitiativeDecisionStatus.PendingDelivery).ToArray() ?? []);
        }
    }

    public Task<int> BeginDecisionAttemptAsync(
        Guid sessionId,
        Guid decisionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            var (decisions, index, decision) = GetInitiativeDecision(
                sessionId,
                decisionId);
            if (decision.Status == InitiativeDecisionStatus.PendingDelivery)
            {
                decision = decision with { AttemptCount = decision.AttemptCount + 1 };
                decisions[index] = decision;
            }

            return Task.FromResult(decision.AttemptCount);
        }
    }

    public Task MarkDecisionStatusAsync(
        Guid sessionId,
        Guid decisionId,
        InitiativeDecisionStatus status,
        DateTimeOffset changedAt,
        string? failureType = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            var (decisions, index, decision) = GetInitiativeDecision(
                sessionId,
                decisionId);
            if (decision.Status != status)
            {
                if (decision.Status != InitiativeDecisionStatus.PendingDelivery)
                {
                    throw new InvalidOperationException(
                        $"Initiative decision '{decisionId}' cannot transition from " +
                        $"'{decision.Status}' to '{status}'.");
                }

                decisions[index] = decision with
                {
                    Status = status,
                    DeliveredAt = status == InitiativeDecisionStatus.Delivered
                        ? changedAt
                        : null,
                    FailedAt = status == InitiativeDecisionStatus.Failed
                        ? changedAt
                        : null,
                    FailureType = status == InitiativeDecisionStatus.Failed
                        ? failureType
                        : null
                };
            }
        }

        return Task.CompletedTask;
    }

    public Task<int> CountDeliveredContactsAsync(
        Guid sessionId,
        Guid participantId,
        DateTimeOffset since,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            return Task.FromResult(_initiativeDeliveries.Values
                .Where(item =>
                    item.SessionId == sessionId &&
                    item.ParticipantId == participantId &&
                    item.Status == InitiativeDeliveryStatus.Delivered &&
                    item.DeliveredAt >= since)
                .Select(item => item.LogicalMessageId)
                .Distinct()
                .Count());
        }
    }

    public Task<IReadOnlyList<InitiativeParticipantPreference>>
        GetParticipantPreferencesAsync(
            Session session,
            CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<InitiativeParticipantPreference>>(
                new[] { session.ParticipantA, session.ParticipantB }
                    .Select(participant => _initiativePreferences.GetValueOrDefault(
                        (session.Id, participant.Id)) ??
                        new InitiativeParticipantPreference(
                            session.Id,
                            participant.Id,
                            true,
                            null,
                            session.CreatedAt))
                    .ToArray());
        }
    }

    public Task SetParticipantEnabledAsync(
        Guid sessionId,
        Guid participantId,
        bool enabled,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            _sessions[sessionId].GetParticipant(participantId);
            _initiativePreferences[(sessionId, participantId)] = new(
                sessionId,
                participantId,
                enabled,
                null,
                changedAt);
        }

        return Task.CompletedTask;
    }

    public Task PauseParticipantAsync(
        Guid sessionId,
        Guid participantId,
        DateTimeOffset pauseUntil,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            _sessions[sessionId].GetParticipant(participantId);
            var existing = _initiativePreferences.GetValueOrDefault(
                (sessionId, participantId));
            _initiativePreferences[(sessionId, participantId)] = new(
                sessionId,
                participantId,
                existing?.IsEnabled ?? true,
                existing?.PauseUntil is DateTimeOffset current && current > pauseUntil
                    ? current
                    : pauseUntil,
                changedAt);
        }

        return Task.CompletedTask;
    }

    public Task<bool> HasDeliveredInitiativeMessageAsync(
        Guid sessionId,
        Guid decisionId,
        Guid participantId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            return Task.FromResult(_initiativeDeliveries.Values.Any(item =>
                item.SessionId == sessionId &&
                item.DecisionId == decisionId &&
                item.ParticipantId == participantId &&
                item.Status == InitiativeDeliveryStatus.Delivered));
        }
    }

    public Task<IReadOnlyList<InitiativeDelivery>> EnsureInitiativeDeliveryPlanAsync(
        InitiativeDeliveryPlan plan,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            var existing = _initiativeDeliveries.Values
                .Where(item =>
                    item.DecisionId == plan.DecisionId &&
                    item.DeliveryKey == plan.DeliveryKey)
                .OrderBy(item => item.ChunkIndex)
                .ToArray();
            if (existing.Length > 0)
            {
                if (existing.Length != plan.Chunks.Count ||
                    existing.Any(item => item.Text != plan.Chunks[item.ChunkIndex - 1]))
                {
                    throw new InvalidOperationException(
                        "Initiative delivery plan conflicts with persisted data.");
                }

                return Task.FromResult<IReadOnlyList<InitiativeDelivery>>(existing);
            }

            var logicalMessageId = Guid.NewGuid();
            var created = plan.Chunks.Select((text, index) => new InitiativeDelivery(
                Guid.NewGuid(),
                plan.DecisionId,
                plan.SessionId,
                plan.ParticipantId,
                logicalMessageId,
                plan.DeliveryKey,
                index + 1,
                plan.Chunks.Count,
                text,
                InitiativeDeliveryStatus.Pending,
                plan.CreatedAt)).ToArray();
            foreach (var delivery in created)
            {
                _initiativeDeliveries.Add(delivery.Id, delivery);
            }

            return Task.FromResult<IReadOnlyList<InitiativeDelivery>>(created);
        }
    }

    public Task MarkInitiativeDeliveryAttemptingAsync(
        Guid sessionId,
        Guid decisionId,
        Guid deliveryId,
        DateTimeOffset attemptedAt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            var delivery = GetInitiativeDelivery(sessionId, decisionId, deliveryId);
            if (delivery.Status != InitiativeDeliveryStatus.Delivered)
            {
                _initiativeDeliveries[deliveryId] = delivery with
                {
                    Status = InitiativeDeliveryStatus.Attempting,
                    AttemptedAt = attemptedAt
                };
            }
        }

        return Task.CompletedTask;
    }

    public Task RecordInitiativeDeliveryAsync(
        Guid sessionId,
        Guid decisionId,
        Guid deliveryId,
        DateTimeOffset deliveredAt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            var delivery = GetInitiativeDelivery(sessionId, decisionId, deliveryId);
            if (delivery.Status == InitiativeDeliveryStatus.Delivered)
            {
                return Task.CompletedTask;
            }

            if (delivery.Status != InitiativeDeliveryStatus.Attempting)
            {
                throw new InvalidOperationException(
                    $"Initiative delivery '{deliveryId}' was not marked attempting.");
            }

            _initiativeDeliveries[deliveryId] = delivery with
            {
                Status = InitiativeDeliveryStatus.Delivered,
                DeliveredAt = deliveredAt
            };
            var chunks = _initiativeDeliveries.Values
                .Where(item =>
                    item.LogicalMessageId == delivery.LogicalMessageId &&
                    item.Status == InitiativeDeliveryStatus.Delivered)
                .OrderBy(item => item.ChunkIndex)
                .ToArray();
            var message = new Message(
                delivery.LogicalMessageId,
                sessionId,
                null,
                delivery.ParticipantId,
                MessageDirection.MediatorToParticipant,
                string.Concat(chunks.Select(item => item.Text)),
                chunks.Min(item => item.DeliveredAt)!.Value);
            var messages = _messagesBySession[sessionId];
            var existingIndex = messages.FindIndex(item =>
                item.Message.Id == delivery.LogicalMessageId);
            if (existingIndex < 0)
            {
                messages.Add(new SequencedMessage(++_nextMessageSequence, message));
            }
            else
            {
                messages[existingIndex] = new SequencedMessage(
                    messages[existingIndex].Sequence,
                    message);
            }
        }

        return Task.CompletedTask;
    }

    private (List<InitiativeDecision> Decisions, int Index, InitiativeDecision Decision)
        GetInitiativeDecision(Guid sessionId, Guid decisionId)
    {
        if (!_initiativeDecisions.TryGetValue(sessionId, out var decisions))
        {
            throw new KeyNotFoundException($"Session '{sessionId}' has no initiative decisions.");
        }

        var index = decisions.FindIndex(item => item.Id == decisionId);
        if (index < 0)
        {
            throw new KeyNotFoundException(
                $"Initiative decision '{decisionId}' was not found.");
        }

        return (decisions, index, decisions[index]);
    }

    private InitiativeDelivery GetInitiativeDelivery(
        Guid sessionId,
        Guid decisionId,
        Guid deliveryId)
    {
        if (!_initiativeDeliveries.TryGetValue(deliveryId, out var delivery) ||
            delivery.SessionId != sessionId || delivery.DecisionId != decisionId)
        {
            throw new KeyNotFoundException(
                $"Initiative delivery '{deliveryId}' was not found.");
        }

        return delivery;
    }
}
