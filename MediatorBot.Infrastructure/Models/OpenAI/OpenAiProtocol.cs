namespace MediatorBot.Infrastructure;

public sealed record OpenAiToolDefinition(
    string Name,
    string Description,
    string ParametersJson);

public sealed record OpenAiToolCall(
    string Name,
    string ArgumentsJson);

public sealed record OpenAiTokenUsage(
    int InputTokens,
    int CachedInputTokens,
    int OutputTokens);

public sealed record OpenAiChatRequest(
    string SystemPrompt,
    string ConversationPrompt,
    IReadOnlyList<OpenAiToolDefinition> Tools,
    int MaxOutputTokens);

public sealed record OpenAiChatResponse(
    IReadOnlyList<OpenAiToolCall> ToolCalls,
    string? AssistantText,
    string Model,
    OpenAiTokenUsage Usage);
