using MediatorBot.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MediatorBot.Telegram;

public sealed class InitiativeHeartbeatHostedService(
    InitiativeEvaluationService evaluationService,
    TelegramInitiativeDispatcher dispatcher,
    TelegramAdapterOptions telegramOptions,
    InitiativeOptions initiativeOptions,
    TimeProvider timeProvider,
    ILogger<InitiativeHeartbeatHostedService> logger) : IHostedService
{
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private Task? _loop;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!initiativeOptions.Enabled)
        {
            logger.LogInformation("Proactive initiative heartbeat is disabled.");
            return;
        }

        await dispatcher.DispatchPendingAsync(
            telegramOptions.SessionId,
            timeProvider.GetUtcNow(),
            cancellationToken);
        _loop = RunAsync(_lifetimeCancellation.Token);
        logger.LogInformation(
            "Proactive initiative heartbeat started. ShadowMode={ShadowMode} " +
            "HeartbeatMinutes={HeartbeatMinutes} TimeZone={TimeZone} " +
            "QuietHours={QuietStart}:00-{QuietEnd}:00 " +
            "MaxContactsPerParticipantPer24Hours={MaxContacts}",
            initiativeOptions.ShadowMode,
            initiativeOptions.HeartbeatInterval.TotalMinutes,
            initiativeOptions.TimeZoneId,
            initiativeOptions.QuietHoursStartHour,
            initiativeOptions.QuietHoursEndHour,
            initiativeOptions.MaxContactsPerParticipantPer24Hours);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _lifetimeCancellation.Cancel();
        if (_loop is not null)
        {
            await _loop.WaitAsync(cancellationToken);
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(
            initiativeOptions.HeartbeatInterval,
            timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await RunHeartbeatAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunHeartbeatAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        try
        {
            await dispatcher.DispatchPendingAsync(
                telegramOptions.SessionId,
                now,
                cancellationToken);
            var decision = await evaluationService.TryEvaluateAsync(
                telegramOptions.SessionId,
                now,
                cancellationToken);
            if (decision?.Status == InitiativeDecisionStatus.PendingDelivery)
            {
                await dispatcher.DispatchPendingAsync(
                    telegramOptions.SessionId,
                    timeProvider.GetUtcNow(),
                    cancellationToken);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Proactive initiative heartbeat failed. SessionId={SessionId} " +
                "ErrorType={ErrorType}",
                telegramOptions.SessionId,
                exception.GetType().Name);
        }
    }
}
