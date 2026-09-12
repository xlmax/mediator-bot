namespace MediatorBot.Core;

public sealed class ConversationCompactionOptions
{
    public bool Enabled { get; init; } = true;

    public int TriggerMessageCount { get; init; } = 200;

    public int TriggerHistoryCharacters { get; init; } = 50_000;

    public int RetainRecentMessageCount { get; init; } = 80;

    public int RetainRecentCharacters { get; init; } = 20_000;

    public int MaxSummaryCharacters { get; init; } = 6_000;

    public int MaxOutputTokens { get; init; } = 2_000;

    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromMinutes(3);

    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMinutes(1);
}
