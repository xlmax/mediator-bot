namespace MediatorBot.Telegram;

public interface ITelegramMessageTransport
{
    Task SendTextMessageAsync(
        long telegramUserId,
        string text,
        CancellationToken cancellationToken = default);
}
