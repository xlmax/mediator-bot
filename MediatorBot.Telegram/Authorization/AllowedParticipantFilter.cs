using Microsoft.Extensions.Logging;
using TeleFlow.Telegram;

namespace MediatorBot.Telegram;

public sealed class AllowedParticipantFilter(
    TelegramParticipantRegistry participantRegistry,
    ILogger<AllowedParticipantFilter> logger)
    : ITelegramFilter<MessageContext>
{
    public async ValueTask<bool> MatchesAsync(
        MessageContext context,
        CancellationToken cancellationToken = default)
    {
        var telegramUserId = context.Sender?.Id;
        if (telegramUserId is null)
        {
            return false;
        }

        var binding = await participantRegistry.FindByTelegramUserIdAsync(
            telegramUserId.Value,
            cancellationToken);
        if (binding is not null)
        {
            return true;
        }

        logger.LogWarning(
            "Unauthorized Telegram update rejected. UpdateId={UpdateId} " +
            "TelegramUserId={TelegramUserId}",
            context.Update.UpdateId,
            telegramUserId.Value);
        return false;
    }
}
