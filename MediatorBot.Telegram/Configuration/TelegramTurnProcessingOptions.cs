namespace MediatorBot.Telegram;

public sealed class TelegramTurnProcessingOptions
{
    public int MaxAttempts { get; init; } = 3;

    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(15);
}
