namespace MediatorBot.Infrastructure;

public sealed class OpenAiModelRuntimeOptions
{
    public required string ApiKey { get; init; }

    public required string Model { get; init; }

    public Uri? Endpoint { get; init; }

    public int MaxOutputTokens { get; init; } = 1500;
}
