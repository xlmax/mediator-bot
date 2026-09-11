using System.Diagnostics;
using MediatorBot.Core;
using Microsoft.Extensions.Logging;

namespace MediatorBot.Telegram;

public sealed class TelegramMediatorActionDispatcher(
    TelegramParticipantRegistry participantRegistry,
    ITelegramMessageTransport transport,
    IMediatorDeliveryRecorder deliveryRecorder,
    IMediatedRequestStore mediatedRequestStore,
    TelegramTextChunker textChunker,
    TelegramAdapterOptions options,
    ILogger<TelegramMediatorActionDispatcher> logger)
{
    public async Task DispatchAsync(
        long updateId,
        long sourceTelegramUserId,
        Session session,
        IReadOnlyList<MediatorAction> actions,
        CancellationToken cancellationToken = default)
    {
        foreach (var action in actions)
        {
            switch (action)
            {
                case SendToParticipant send:
                    await DeliverAsync(
                        updateId,
                        sourceTelegramUserId,
                        session,
                        send.ParticipantId,
                        send.Text,
                        nameof(SendToParticipant),
                        send.DisclosureDecision,
                        cancellationToken);
                    break;

                case SendToBoth send:
                    await DeliverAsync(
                        updateId,
                        sourceTelegramUserId,
                        session,
                        session.ParticipantA.Id,
                        send.TextForParticipantA,
                        nameof(SendToBoth),
                        send.DisclosureDecision,
                        cancellationToken);
                    await DeliverAsync(
                        updateId,
                        sourceTelegramUserId,
                        session,
                        session.ParticipantB.Id,
                        send.TextForParticipantB,
                        nameof(SendToBoth),
                        send.DisclosureDecision,
                        cancellationToken);
                    break;

                case OpenMediatedRequest open:
                    await OpenRequestAsync(
                        updateId,
                        sourceTelegramUserId,
                        session,
                        open,
                        cancellationToken);
                    break;

                case ResolveMediatedRequest resolve:
                    await ResolveRequestAsync(
                        updateId,
                        sourceTelegramUserId,
                        session,
                        resolve,
                        cancellationToken);
                    break;

                case CancelMediatedRequest cancel:
                    await CancelRequestAsync(
                        updateId,
                        sourceTelegramUserId,
                        session,
                        cancel,
                        cancellationToken);
                    break;

                case NoAction:
                    logger.LogInformation(
                        "Mediator action completed. UpdateId={UpdateId} " +
                        "TelegramUserId={TelegramUserId} SessionId={SessionId} " +
                        "MediatorAction={MediatorAction} " +
                        "DisclosureDecision={DisclosureDecision} " +
                        "DeliveryResult={DeliveryResult} DurationMs={DurationMs:F1}",
                        updateId,
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
        long updateId,
        long sourceTelegramUserId,
        Session session,
        OpenMediatedRequest action,
        CancellationToken cancellationToken)
    {
        var request = new MediatedRequest(
            action.RequestId,
            session.Id,
            action.RequesterId,
            action.RespondentId,
            action.Summary,
            MediatedRequestStatus.PendingDelivery,
            DateTimeOffset.UtcNow);
        await mediatedRequestStore.CreateAsync(request, cancellationToken);

        await DeliverAsync(
            updateId,
            sourceTelegramUserId,
            session,
            action.RespondentId,
            action.TextForRespondent,
            nameof(OpenMediatedRequest),
            action.DisclosureDecision,
            cancellationToken);
        await PersistRequestTransitionAsync(
            updateId,
            sourceTelegramUserId,
            session.Id,
            action.RequestId,
            "AwaitingResponse",
            token => mediatedRequestStore.MarkAwaitingResponseAsync(
                session.Id,
                action.RequestId,
                token));
        await DeliverAsync(
            updateId,
            sourceTelegramUserId,
            session,
            action.RequesterId,
            action.TextForRequester,
            nameof(OpenMediatedRequest),
            action.DisclosureDecision,
            cancellationToken);
    }

    private async Task ResolveRequestAsync(
        long updateId,
        long sourceTelegramUserId,
        Session session,
        ResolveMediatedRequest action,
        CancellationToken cancellationToken)
    {
        await DeliverAsync(
            updateId,
            sourceTelegramUserId,
            session,
            action.RequesterId,
            action.TextForRequester,
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
            updateId,
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
                updateId,
                sourceTelegramUserId,
                session,
                action.RespondentId,
                action.TextForRespondent,
                nameof(ResolveMediatedRequest),
                action.DisclosureDecision,
                cancellationToken);
        }
    }

    private async Task CancelRequestAsync(
        long updateId,
        long sourceTelegramUserId,
        Session session,
        CancelMediatedRequest action,
        CancellationToken cancellationToken)
    {
        await DeliverAsync(
            updateId,
            sourceTelegramUserId,
            session,
            action.RespondentId,
            action.TextForRespondent,
            nameof(CancelMediatedRequest),
            action.DisclosureDecision,
            cancellationToken);
        await PersistRequestTransitionAsync(
            updateId,
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
            updateId,
            sourceTelegramUserId,
            session,
            action.RequesterId,
            action.TextForRequester,
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
            var transitionTask = transition(persistenceCancellation.Token);
            await transitionTask.WaitAsync(persistenceCancellation.Token);
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
        long updateId,
        long sourceTelegramUserId,
        Session session,
        Guid participantId,
        string text,
        string actionType,
        DisclosureDecision disclosureDecision,
        CancellationToken cancellationToken)
    {
        var chunks = textChunker.Split(text);
        for (var index = 0; index < chunks.Count; index++)
        {
            await DeliverChunkAsync(
                updateId,
                sourceTelegramUserId,
                session,
                participantId,
                chunks[index],
                actionType,
                disclosureDecision,
                index + 1,
                chunks.Count,
                cancellationToken);
        }
    }

    private async Task DeliverChunkAsync(
        long updateId,
        long sourceTelegramUserId,
        Session session,
        Guid participantId,
        string text,
        string actionType,
        DisclosureDecision disclosureDecision,
        int chunkIndex,
        int chunkCount,
        CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var telegramUserId = await participantRegistry.GetTelegramUserIdAsync(
            participantId,
            cancellationToken);

        try
        {
            using var deliveryCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deliveryCancellation.CancelAfter(options.DeliveryTimeout);
            var deliveryTask = transport.SendTextMessageAsync(
                telegramUserId,
                text,
                deliveryCancellation.Token);
            await deliveryTask.WaitAsync(deliveryCancellation.Token);
        }
        catch (Exception exception)
        {
            // Telegram/provider errors can contain request details. Log only technical data.
            logger.LogError(
                "Mediator action delivery failed. UpdateId={UpdateId} " +
                "TelegramUserId={TelegramUserId} ParticipantId={ParticipantId} " +
                "SessionId={SessionId} MediatorAction={MediatorAction} " +
                "DisclosureDecision={DisclosureDecision} " +
                "DeliveryResult={DeliveryResult} ChunkIndex={ChunkIndex} " +
                "ChunkCount={ChunkCount} DurationMs={DurationMs:F1} " +
                "ErrorType={ErrorType}",
                updateId,
                telegramUserId,
                participantId,
                session.Id,
                actionType,
                disclosureDecision,
                "DeliveryFailed",
                chunkIndex,
                chunkCount,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                exception.GetType().Name);
            throw;
        }

        try
        {
            using var recordingCancellation = new CancellationTokenSource(
                options.DeliveryRecordingTimeout);
            var recordingTask = deliveryRecorder.RecordDeliveredAsync(
                session.Id,
                participantId,
                text,
                recordingCancellation.Token);
            await recordingTask.WaitAsync(recordingCancellation.Token);
        }
        catch (Exception exception)
        {
            logger.LogCritical(
                "Mediator action was delivered but history recording failed. " +
                "UpdateId={UpdateId} TelegramUserId={TelegramUserId} " +
                "ParticipantId={ParticipantId} SessionId={SessionId} " +
                "MediatorAction={MediatorAction} " +
                "DisclosureDecision={DisclosureDecision} " +
                "DeliveryResult={DeliveryResult} ChunkIndex={ChunkIndex} ChunkCount={ChunkCount} " +
                "DurationMs={DurationMs:F1} ErrorType={ErrorType}",
                updateId,
                telegramUserId,
                participantId,
                session.Id,
                actionType,
                disclosureDecision,
                "DeliveredButNotRecorded",
                chunkIndex,
                chunkCount,
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
            updateId,
            telegramUserId,
            participantId,
            session.Id,
            actionType,
            disclosureDecision,
            "Delivered",
            chunkIndex,
            chunkCount,
            Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
    }
}
