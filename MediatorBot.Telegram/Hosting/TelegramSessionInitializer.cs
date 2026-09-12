using Microsoft.Extensions.Hosting;

namespace MediatorBot.Telegram;

public sealed class TelegramSessionInitializer(
    TelegramParticipantRegistry participantRegistry,
    TelegramSessionWorkQueue workQueue,
    TelegramAdapterOptions options) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await participantRegistry.InitializeAsync(cancellationToken);
        await workQueue.RecoverSessionAsync(options.SessionId, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        workQueue.StopAsync(cancellationToken);
}
