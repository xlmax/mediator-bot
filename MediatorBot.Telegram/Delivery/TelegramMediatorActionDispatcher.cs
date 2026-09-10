using System.Diagnostics;
using MediatorBot.Core;
using Microsoft.Extensions.Logging;

namespace MediatorBot.Telegram;

public sealed class TelegramMediatorActionDispatcher(
    TelegramParticipantRegistry participantRegistry,
    ITelegramMessageTransport transport,
    IMediatorDeliveryRecorder deliveryRecorder,
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
                        cancellationToken);
                    await DeliverAsync(
                        updateId,
                        sourceTelegramUserId,
                        session,
                        session.ParticipantB.Id,
                        send.TextForParticipantB,
                        nameof(SendToBoth),
                        cancellationToken);
                    break;

                case NoAction:
                    logger.LogInformation(
                        "Mediator action completed. UpdateId={UpdateId} " +
                        "TelegramUserId={TelegramUserId} SessionId={SessionId} " +
                        "MediatorAction={MediatorAction} DeliveryResult={DeliveryResult} " +
                        "DurationMs={DurationMs:F1}",
                        updateId,
                        sourceTelegramUserId,
                        session.Id,
                        nameof(NoAction),
                        "NoSend",
                        0d);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unsupported mediator action '{action.GetType().Name}'.");
            }
        }
    }

    private async Task DeliverAsync(
        long updateId,
        long sourceTelegramUserId,
        Session session,
        Guid participantId,
        string text,
        string actionType,
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

            await deliveryRecorder.RecordDeliveredAsync(
                session.Id,
                participantId,
                text,
                cancellationToken);

            logger.LogInformation(
                "Mediator action delivered. UpdateId={UpdateId} " +
                "TelegramUserId={TelegramUserId} ParticipantId={ParticipantId} " +
                "SessionId={SessionId} MediatorAction={MediatorAction} " +
                "DeliveryResult={DeliveryResult} DurationMs={DurationMs:F1}",
                updateId,
                telegramUserId,
                participantId,
                session.Id,
                actionType,
                "Delivered",
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        }
        catch (Exception exception)
        {
            // Telegram/provider errors can contain request details. Log only technical data.
            logger.LogError(
                "Mediator action delivery failed. UpdateId={UpdateId} " +
                "TelegramUserId={TelegramUserId} ParticipantId={ParticipantId} " +
                "SessionId={SessionId} MediatorAction={MediatorAction} " +
                "DeliveryResult={DeliveryResult} DurationMs={DurationMs:F1} " +
                "ErrorType={ErrorType}",
                updateId,
                telegramUserId,
                participantId,
                session.Id,
                actionType,
                "Failed",
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                exception.GetType().Name);
            throw;
        }
    }
}
