namespace MediatorBot.Infrastructure;

public sealed class OpenAiModelRuntimeOptions
{
    public required string ApiKey { get; init; }

    public required string Model { get; init; }

    public Uri? Endpoint { get; init; }

    public int MaxOutputTokens { get; init; } = 1500;

    public int MaxAttempts { get; init; } = 3;

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(120);

    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan RetryMaxDelay { get; init; } = TimeSpan.FromSeconds(30);
}
