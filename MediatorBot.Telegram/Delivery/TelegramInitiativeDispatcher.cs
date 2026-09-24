using System.Diagnostics;
using MediatorBot.Core;
using Microsoft.Extensions.Logging;

namespace MediatorBot.Telegram;

public sealed class TelegramInitiativeDispatcher(
    TelegramParticipantRegistry participantRegistry,
    ITelegramMessageTransport transport,
    IInitiativeStore initiativeStore,
    IInitiativeDeliveryStore deliveryStore,
    IExternalTurnQueueStore turnQueueStore,
    IConversationStore conversationStore,
    TelegramTextChunker textChunker,
    TelegramAdapterOptions telegramOptions,
    InitiativeOptions initiativeOptions,
    InitiativeEvaluationService evaluationService,
    ILogger<TelegramInitiativeDispatcher> logger)
{
    public async Task DispatchPendingAsync(
        Guid sessionId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var decisions = await initiativeStore.GetPendingDeliveryDecisionsAsync(
            sessionId,
            cancellationToken);
        foreach (var decision in decisions)
        {
            await DispatchAsync(decision, now, cancellationToken);
        }
    }

    private async Task DispatchAsync(
        InitiativeDecision decision,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var session = await LoadSessionAsync(decision.SessionId, cancellationToken);
        var targets = GetTargets(session, decision);
        var undeliveredTargets = new List<Participant>();
        foreach (var target in targets)
        {
            if (!await deliveryStore.HasDeliveredInitiativeMessageAsync(
                    decision.SessionId,
                    decision.Id,
                    target.Id,
                    cancellationToken))
            {
                undeliveredTargets.Add(target);
            }
        }

        if (undeliveredTargets.Count == 0)
        {
            await initiativeStore.MarkDecisionStatusAsync(
                decision.SessionId,
                decision.Id,
                InitiativeDecisionStatus.Delivered,
                now,
                cancellationToken: cancellationToken);
            return;
        }

        if (evaluationService.IsQuietHours(now))
        {
            return;
        }

        var pendingTurns = await turnQueueStore.GetPendingAsync(
            decision.SessionId,
            cancellationToken);
        var latestParticipantMessageId = await initiativeStore
            .GetLatestParticipantMessageIdAsync(decision.SessionId, cancellationToken);
        if (pendingTurns.Count > 0 ||
            latestParticipantMessageId != decision.ObservedParticipantMessageId)
        {
            await initiativeStore.MarkDecisionStatusAsync(
                decision.SessionId,
                decision.Id,
                InitiativeDecisionStatus.Superseded,
                now,
                cancellationToken: cancellationToken);
            return;
        }

        var preferences = await initiativeStore.GetParticipantPreferencesAsync(
            session,
            cancellationToken);
        foreach (var target in undeliveredTargets)
        {
            var preference = preferences.Single(item => item.ParticipantId == target.Id);
            var delivered = await initiativeStore.CountDeliveredContactsAsync(
                session.Id,
                target.Id,
                now.AddHours(-24),
                cancellationToken);
            if (!preference.AllowsContact(now) ||
                delivered >= initiativeOptions.MaxContactsPerParticipantPer24Hours)
            {
                await initiativeStore.MarkDecisionStatusAsync(
                    decision.SessionId,
                    decision.Id,
                    InitiativeDecisionStatus.Suppressed,
                    now,
                    cancellationToken: cancellationToken);
                return;
            }
        }

        var attempt = await initiativeStore.BeginDecisionAttemptAsync(
            decision.SessionId,
            decision.Id,
            cancellationToken);
        if (attempt > initiativeOptions.MaxDeliveryAttempts)
        {
            await initiativeStore.MarkDecisionStatusAsync(
                decision.SessionId,
                decision.Id,
                InitiativeDecisionStatus.Failed,
                now,
                "RetryLimitExceeded",
                cancellationToken);
            return;
        }

        try
        {
            foreach (var target in targets)
            {
                var text = target.Id == session.ParticipantA.Id
                    ? decision.TextForParticipantA
                    : decision.TextForParticipantB;
                await DeliverAsync(
                    decision,
                    target,
                    text ?? throw new InvalidDataException(
                        "Initiative decision is missing target text."),
                    target.Id == session.ParticipantA.Id ? "participant-a" : "participant-b",
                    now,
                    cancellationToken);
            }

            await initiativeStore.MarkDecisionStatusAsync(
                decision.SessionId,
                decision.Id,
                InitiativeDecisionStatus.Delivered,
                now,
                cancellationToken: cancellationToken);
            logger.LogInformation(
                "Proactive initiative delivered. SessionId={SessionId} " +
                "DecisionId={DecisionId} Decision={Decision} Intent={Intent} " +
                "TargetCount={TargetCount} Attempt={Attempt}",
                decision.SessionId,
                decision.Id,
                decision.DecisionKind,
                decision.Intent,
                targets.Count,
                attempt);
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Proactive initiative delivery failed. SessionId={SessionId} " +
                "DecisionId={DecisionId} Attempt={Attempt} ErrorType={ErrorType}",
                decision.SessionId,
                decision.Id,
                attempt,
                exception.GetType().Name);
            if (attempt >= initiativeOptions.MaxDeliveryAttempts)
            {
                await initiativeStore.MarkDecisionStatusAsync(
                    decision.SessionId,
                    decision.Id,
                    InitiativeDecisionStatus.Failed,
                    now,
                    exception.GetType().Name,
                    cancellationToken);
            }
        }
    }

    private async Task DeliverAsync(
        InitiativeDecision decision,
        Participant participant,
        string text,
        string deliveryKey,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var deliveries = await deliveryStore.EnsureInitiativeDeliveryPlanAsync(
            new InitiativeDeliveryPlan(
                decision.Id,
                decision.SessionId,
                participant.Id,
                deliveryKey,
                textChunker.Split(text),
                decision.EvaluatedAt),
            cancellationToken);
        foreach (var delivery in deliveries.OrderBy(item => item.ChunkIndex))
        {
            if (delivery.Status == InitiativeDeliveryStatus.Delivered)
            {
                continue;
            }

            await DeliverChunkAsync(decision, delivery, now, cancellationToken);
        }
    }

    private async Task DeliverChunkAsync(
        InitiativeDecision decision,
        InitiativeDelivery delivery,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var telegramUserId = await participantRegistry.GetTelegramUserIdAsync(
            delivery.ParticipantId,
            cancellationToken);
        await deliveryStore.MarkInitiativeDeliveryAttemptingAsync(
            decision.SessionId,
            decision.Id,
            delivery.Id,
            now,
            cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeout.CancelAfter(telegramOptions.DeliveryTimeout);
            await transport.SendTextMessageAsync(
                telegramUserId,
                delivery.Text,
                timeout.Token).WaitAsync(timeout.Token);
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Proactive initiative chunk delivery failed. SessionId={SessionId} " +
                "DecisionId={DecisionId} ParticipantId={ParticipantId} " +
                "ChunkIndex={ChunkIndex} ChunkCount={ChunkCount} " +
                "DurationMs={DurationMs:F1} ErrorType={ErrorType}",
                decision.SessionId,
                decision.Id,
                delivery.ParticipantId,
                delivery.ChunkIndex,
                delivery.ChunkCount,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                exception.GetType().Name);
            throw;
        }

        using var recordingTimeout = new CancellationTokenSource(
            telegramOptions.DeliveryRecordingTimeout);
        await deliveryStore.RecordInitiativeDeliveryAsync(
            decision.SessionId,
            decision.Id,
            delivery.Id,
            now,
            recordingTimeout.Token).WaitAsync(recordingTimeout.Token);
    }

    private async Task<Session> LoadSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken) =>
        await conversationStore.GetSessionAsync(sessionId, cancellationToken)
        ?? throw new KeyNotFoundException($"Session '{sessionId}' was not found.");

    private static IReadOnlyList<Participant> GetTargets(
        Session session,
        InitiativeDecision decision) =>
        decision.DecisionKind switch
        {
            InitiativeDecisionKind.ContactParticipant =>
                [session.GetParticipant(decision.TargetParticipantId!.Value)],
            InitiativeDecisionKind.ContactBoth =>
                [session.ParticipantA, session.ParticipantB],
            _ => throw new InvalidOperationException(
                $"Decision '{decision.DecisionKind}' cannot be delivered.")
        };
}
