using MediatorBot.Core;

namespace MediatorBot.Telegram;

public interface ITelegramQueuedTurnProcessor
{
    Task<TelegramMessageProcessingStatus> ProcessAsync(
        PendingExternalTurn turn,
        CancellationToken cancellationToken = default);
}
