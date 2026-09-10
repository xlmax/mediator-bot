using TeleFlow.Telegram;
using TeleFlow.Telegram.Schema.Abstractions;

namespace MediatorBot.Telegram;

public sealed class TeleFlowMessageTransport(ITelegramClient telegramClient)
    : ITelegramMessageTransport
{
    public async Task SendTextMessageAsync(
        long telegramUserId,
        string text,
        CancellationToken cancellationToken = default)
    {
        await telegramClient.SendMessageAsync(
            IntegerString.From(telegramUserId),
            text,
            protectContent: true,
            cancellationToken: cancellationToken);
    }
}
