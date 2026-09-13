using System.Globalization;
using MediatorBot.Core;
using Microsoft.Extensions.Logging;

namespace MediatorBot.Telegram;

public enum TelegramMessageProcessingStatus
{
    Processed,
    Duplicate,
    UnknownUser,
    ModelUnavailable,
    ModelProtocolFailure,
    Deferred,
    ProcessingFailed
}

public sealed class TelegramMessageProcessor(
    TelegramParticipantRegistry participantRegistry,
    IExternalTurnQueueStore turnQueueStore,
    TelegramSessionWorkQueue workQueue,
    ILogger<TelegramMessageProcessor> logger)
{
    public async Task<TelegramMessageProcessingStatus> ProcessPrivateTextAsync(
        long updateId,
        long telegramUserId,
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var binding = await participantRegistry.FindByTelegramUserIdAsync(
            telegramUserId,
            cancellationToken);

        if (binding is null)
        {
            logger.LogWarning(
                "Telegram user rejected. UpdateId={UpdateId} " +
                "TelegramUserId={TelegramUserId}",
                updateId,
                telegramUserId);
            return TelegramMessageProcessingStatus.UnknownUser;
        }

        var turn = new PendingExternalTurn(
            Guid.NewGuid(),
            binding.Session.Id,
            binding.Participant.Id,
            "telegram",
            updateId.ToString(CultureInfo.InvariantCulture),
            updateId,
            telegramUserId.ToString(CultureInfo.InvariantCulture),
            text,
            DateTimeOffset.UtcNow);
        var enqueued = await turnQueueStore.TryEnqueueAsync(turn, cancellationToken);
        if (!enqueued)
        {
            await workQueue.RecoverSessionAsync(binding.Session.Id, cancellationToken);
            logger.LogInformation(
                "Duplicate Telegram update ignored. UpdateId={UpdateId} " +
                "TelegramUserId={TelegramUserId} SessionId={SessionId}",
                updateId,
                telegramUserId,
                binding.Session.Id);
            return TelegramMessageProcessingStatus.Duplicate;
        }

        return await workQueue.ProcessEnqueuedAsync(turn, cancellationToken);
    }
}
