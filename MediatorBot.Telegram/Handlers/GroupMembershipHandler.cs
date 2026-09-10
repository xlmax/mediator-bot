using Microsoft.Extensions.Logging;
using TeleFlow.Annotations;
using TeleFlow.Telegram;
using TeleFlow.Telegram.Schema.Abstractions;

namespace MediatorBot.Telegram;

public sealed class GroupMembershipHandler(
    ILogger<GroupMembershipHandler> logger)
{
    [MyChatMemberUpdated]
    [ChatMemberTransition(TelegramMemberTransition.Join)]
    [ChatType(
        TelegramChatType.Group,
        TelegramChatType.Supergroup,
        TelegramChatType.Channel)]
    public async Task LeaveAsync(
        ChatMemberUpdatedContext context,
        CancellationToken cancellationToken)
    {
        await context.Bot.LeaveChatAsync(
            IntegerString.From(context.TelegramChat.Id),
            cancellationToken);

        logger.LogWarning(
            "Bot left a non-private chat. UpdateId={UpdateId} ChatId={ChatId}",
            context.Update.UpdateId,
            context.TelegramChat.Id);
    }
}
