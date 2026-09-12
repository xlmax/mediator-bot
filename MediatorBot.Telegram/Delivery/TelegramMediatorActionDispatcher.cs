using System.Diagnostics;
using System.Globalization;
using MediatorBot.Core;
using Microsoft.Extensions.Logging;

namespace MediatorBot.Telegram;

public sealed class TelegramMediatorActionDispatcher(
    TelegramParticipantRegistry participantRegistry,
    ITelegramMessageTransport transport,
    ITurnExecutionStore turnExecutionStore,
    IMediatedRequestStore mediatedRequestStore,
    TelegramTextChunker textChunker,
    TelegramAdapterOptions options,
    ILogger<TelegramMediatorActionDispatcher> logger)
{
    public async Task DispatchAsync(
        PendingExternalTurn turn,
        Session session,
        IReadOnlyList<MediatorAction> actions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(actions);
        var sourceTelegramUserId = long.Parse(
            turn.ExternalUserId,
            CultureInfo.InvariantCulture);

        for (var actionIndex = 0; actionIndex < actions.Count; actionIndex++)
        {
            var action = actions[actionIndex];
            switch (action)
            {
                case SendToParticipant send:
                    await DeliverAsync(
                        turn,
                        sourceTelegramUserId,
                        session,
                        send.ParticipantId,
                        send.Text,
                        $"{actionIndex}:participant",
                        nameof(SendToParticipant),
                        send.DisclosureDecision,
                        cancellationToken);
                    break;

                case SendToBoth send:
                    await DeliverAsync(
                        turn,
                        sourceTelegramUserId,
                        session,
                        session.ParticipantA.Id,
                        send.TextForParticipantA,
                        $"{actionIndex}:participant-a",
                        nameof(SendToBoth),
                        send.DisclosureDecision,
                        cancellationToken);
                    await DeliverAsync(
                        turn,
                        sourceTelegramUserId,
                        session,
                        session.ParticipantB.Id,
                        send.TextForParticipantB,
                        $"{actionIndex}:participant-b",
                        nameof(SendToBoth),
                        send.DisclosureDecision,
                        cancellationToken);
                    break;

                case OpenMediatedRequest open:
                    await OpenRequestAsync(
                        turn,
                        sourceTelegramUserId,
                        session,
                        open,
                        actionIndex,
                        cancellationToken);
                    break;

                case ResolveMediatedRequest resolve:
                    await ResolveRequestAsync(
                        turn,
                        sourceTelegramUserId,
                        session,
                        resolve,
                        actionIndex,
                        cancellationToken);
                    break;

                case CancelMediatedRequest cancel:
                    await CancelRequestAsync(
                        turn,
                        sourceTelegramUserId,
                        session,
                        cancel,
                        actionIndex,
                        cancellationToken);
                    break;

                case NoAction:
                    logger.LogInformation(
                        "Mediator action completed. UpdateId={UpdateId} " +
                        "TelegramUserId={TelegramUserId} SessionId={SessionId} " +
                        "MediatorAction={MediatorAction} " +
                        "DisclosureDecision={DisclosureDecision} " +
                        "DeliveryResult={DeliveryResult} DurationMs={DurationMs:F1}",
                        turn.SourceSequence,
                        sourceTelegramUserId,
                        session.Id,
                        nameof(NoAction),
                        DisclosureDecision.NoAction,
                        "NoSend",
                        0d);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unsupported mediator action '{action.GetType().Name}'.");
            }
        }
    }

    private async Task OpenRequestAsync(
        PendingExternalTurn turn,
        long sourceTelegramUserId,
        Session session,
        OpenMediatedRequest action,
        int actionIndex,
        CancellationToken cancellationToken)
    {
        var request = new MediatedRequest(
            action.RequestId,
            session.Id,
            action.RequesterId,
            action.RespondentId,
            action.Summary,
            MediatedRequestStatus.PendingDelivery,
            turn.CreatedAt);
        await mediatedRequestStore.CreateAsync(request, cancellationToken);

        await DeliverAsync(
            turn,
            sourceTelegramUserId,
            session,
            action.RespondentId,
            action.TextForRespondent,
            $"{actionIndex}:open:respondent",
            nameof(OpenMediatedRequest),
            action.DisclosureDecision,
            cancellationToken);
        await PersistRequestTransitionAsync(
            turn.SourceSequence,
            sourceTelegramUserId,
            session.Id,
            action.RequestId,
            "AwaitingResponse",
            token => mediatedRequestStore.MarkAwaitingResponseAsync(
                session.Id,
                action.RequestId,
                token));
        await DeliverAsync(
            turn,
            sourceTelegramUserId,
            session,
            action.RequesterId,
            action.TextForRequester,
            $"{actionIndex}:open:requester",
            nameof(OpenMediatedRequest),
            action.DisclosureDecision,
            cancellationToken);
    }

    private async Task ResolveRequestAsync(
        PendingExternalTurn turn,
        long sourceTelegramUserId,
        Session session,
        ResolveMediatedRequest action,
        int actionIndex,
        CancellationToken cancellationToken)
    {
        await DeliverAsync(
            turn,
            sourceTelegramUserId,
            session,
            action.RequesterId,
            action.TextForRequester,
            $"{actionIndex}:resolve:requester",
            nameof(ResolveMediatedRequest),
            action.DisclosureDecision,
            cancellationToken);
        var finalStatus = action.Outcome switch
        {
            MediatedRequestOutcome.Answered => MediatedRequestStatus.Answered,
            MediatedRequestOutcome.Declined => MediatedRequestStatus.Declined,
            MediatedRequestOutcome.NoShareableAnswer =>
                MediatedRequestStatus.NoShareableAnswer,
            _ => throw new InvalidOperationException(
                $"Unsupported mediated request outcome '{action.Outcome}'.")
        };
        await PersistRequestTransitionAsync(
            turn.SourceSequence,
            sourceTelegramUserId,
            session.Id,
            action.RequestId,
            finalStatus.ToString(),
            token => mediatedRequestStore.ResolveAsync(
                session.Id,
                action.RequestId,
                finalStatus,
                DateTimeOffset.UtcNow,
                token));

        if (action.TextForRespondent is not null)
        {
            await DeliverAsync(
                turn,
                sourceTelegramUserId,
                session,
                action.RespondentId,
                action.TextForRespondent,
                $"{actionIndex}:resolve:respondent",
                nameof(ResolveMediatedRequest),
                action.DisclosureDecision,
                cancellationToken);
        }
    }

    private async Task CancelRequestAsync(
        PendingExternalTurn turn,
        long sourceTelegramUserId,
        Session session,
        CancelMediatedRequest action,
        int actionIndex,
        CancellationToken cancellationToken)
    {
        await DeliverAsync(
            turn,
            sourceTelegramUserId,
            session,
            action.RespondentId,
            action.TextForRespondent,
            $"{actionIndex}:cancel:respondent",
            nameof(CancelMediatedRequest),
            action.DisclosureDecision,
            cancellationToken);
        await PersistRequestTransitionAsync(
            turn.SourceSequence,
            sourceTelegramUserId,
            session.Id,
            action.RequestId,
            MediatedRequestStatus.Cancelled.ToString(),
            token => mediatedRequestStore.ResolveAsync(
                session.Id,
                action.RequestId,
                MediatedRequestStatus.Cancelled,
                DateTimeOffset.UtcNow,
                token));
        await DeliverAsync(
            turn,
            sourceTelegramUserId,
            session,
            action.RequesterId,
            action.TextForRequester,
            $"{actionIndex}:cancel:requester",
            nameof(CancelMediatedRequest),
            action.DisclosureDecision,
            cancellationToken);
    }

    private async Task PersistRequestTransitionAsync(
        long updateId,
        long sourceTelegramUserId,
        Guid sessionId,
        Guid requestId,
        string requestStatus,
        Func<CancellationToken, Task> transition)
    {
        try
        {
            using var persistenceCancellation = new CancellationTokenSource(
                options.DeliveryRecordingTimeout);
            await transition(persistenceCancellation.Token)
                .WaitAsync(persistenceCancellation.Token);
        }
        catch (Exception exception)
        {
            logger.LogCritical(
                "Mediator request delivery succeeded but state transition failed. " +
                "UpdateId={UpdateId} TelegramUserId={TelegramUserId} " +
                "SessionId={SessionId} RequestId={RequestId} " +
                "RequestStatus={RequestStatus} ErrorType={ErrorType}",
                updateId,
                sourceTelegramUserId,
                sessionId,
                requestId,
                requestStatus,
                exception.GetType().Name);
            throw;
        }
    }

    private async Task DeliverAsync(
        PendingExternalTurn turn,
        long sourceTelegramUserId,
        Session session,
        Guid participantId,
        string text,
        string deliveryKey,
        string actionType,
        DisclosureDecision disclosureDecision,
        CancellationToken cancellationToken)
    {
        var chunks = textChunker.Split(text);
        var deliveries = await turnExecutionStore.EnsureDeliveryPlanAsync(
            new TurnDeliveryPlan(
                turn.Id,
                session.Id,
                participantId,
                deliveryKey,
                chunks,
                actionType,
                disclosureDecision,
                turn.CreatedAt),
            cancellationToken);
        foreach (var delivery in deliveries.OrderBy(delivery => delivery.ChunkIndex))
        {
            if (delivery.Status == TurnDeliveryStatus.Delivered)
            {
                logger.LogInformation(
                    "Previously delivered mediator action chunk skipped during recovery. " +
                    "UpdateId={UpdateId} ParticipantId={ParticipantId} " +
                    "SessionId={SessionId} MediatorAction={MediatorAction} " +
                    "ChunkIndex={ChunkIndex} ChunkCount={ChunkCount}",
                    turn.SourceSequence,
                    participantId,
                    session.Id,
                    actionType,
                    delivery.ChunkIndex,
                    delivery.ChunkCount);
                continue;
            }

            await DeliverChunkAsync(
                turn,
                sourceTelegramUserId,
                session,
                delivery,
                cancellationToken);
        }
    }

    private async Task DeliverChunkAsync(
        PendingExternalTurn turn,
        long sourceTelegramUserId,
        Session session,
        TurnDelivery delivery,
        CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var telegramUserId = await participantRegistry.GetTelegramUserIdAsync(
            delivery.ParticipantId,
            cancellationToken);
        await turnExecutionStore.MarkDeliveryAttemptingAsync(
            session.Id,
            turn.Id,
            delivery.Id,
            DateTimeOffset.UtcNow,
            cancellationToken);

        try
        {
            using var deliveryCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deliveryCancellation.CancelAfter(options.DeliveryTimeout);
            await transport.SendTextMessageAsync(
                telegramUserId,
                delivery.Text,
                deliveryCancellation.Token).WaitAsync(deliveryCancellation.Token);
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Mediator action delivery failed. UpdateId={UpdateId} " +
                "TelegramUserId={TelegramUserId} ParticipantId={ParticipantId} " +
                "SessionId={SessionId} MediatorAction={MediatorAction} " +
                "DisclosureDecision={DisclosureDecision} " +
                "DeliveryResult={DeliveryResult} ChunkIndex={ChunkIndex} " +
                "ChunkCount={ChunkCount} DurationMs={DurationMs:F1} " +
                "ErrorType={ErrorType}",
                turn.SourceSequence,
                telegramUserId,
                delivery.ParticipantId,
                session.Id,
                delivery.ActionType,
                delivery.DisclosureDecision,
                "DeliveryFailed",
                delivery.ChunkIndex,
                delivery.ChunkCount,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                exception.GetType().Name);
            throw;
        }

        try
        {
            using var recordingCancellation = new CancellationTokenSource(
                options.DeliveryRecordingTimeout);
            await turnExecutionStore.RecordDeliveryAsync(
                session.Id,
                turn.Id,
                delivery.Id,
                DateTimeOffset.UtcNow,
                recordingCancellation.Token).WaitAsync(recordingCancellation.Token);
        }
        catch (Exception exception)
        {
            logger.LogCritical(
                "Mediator action was delivered but outbox recording failed. " +
                "UpdateId={UpdateId} TelegramUserId={TelegramUserId} " +
                "ParticipantId={ParticipantId} SessionId={SessionId} " +
                "MediatorAction={MediatorAction} " +
                "DisclosureDecision={DisclosureDecision} " +
                "DeliveryResult={DeliveryResult} ChunkIndex={ChunkIndex} " +
                "ChunkCount={ChunkCount} DurationMs={DurationMs:F1} ErrorType={ErrorType}",
                turn.SourceSequence,
                telegramUserId,
                delivery.ParticipantId,
                session.Id,
                delivery.ActionType,
                delivery.DisclosureDecision,
                "DeliveredButNotRecorded",
                delivery.ChunkIndex,
                delivery.ChunkCount,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                exception.GetType().Name);
            throw;
        }

        logger.LogInformation(
            "Mediator action delivered. UpdateId={UpdateId} " +
            "TelegramUserId={TelegramUserId} ParticipantId={ParticipantId} " +
            "SessionId={SessionId} MediatorAction={MediatorAction} " +
            "DisclosureDecision={DisclosureDecision} " +
            "DeliveryResult={DeliveryResult} ChunkIndex={ChunkIndex} " +
            "ChunkCount={ChunkCount} DurationMs={DurationMs:F1}",
            turn.SourceSequence,
            telegramUserId,
            delivery.ParticipantId,
            session.Id,
            delivery.ActionType,
            delivery.DisclosureDecision,
            "DeliveredAndRecorded",
            delivery.ChunkIndex,
            delivery.ChunkCount,
            Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
    }
}
