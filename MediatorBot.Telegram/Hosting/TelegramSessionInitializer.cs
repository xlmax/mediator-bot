using Microsoft.Extensions.Hosting;

namespace MediatorBot.Telegram;

public sealed class TelegramSessionInitializer(
    TelegramParticipantRegistry participantRegistry) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        participantRegistry.InitializeAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
