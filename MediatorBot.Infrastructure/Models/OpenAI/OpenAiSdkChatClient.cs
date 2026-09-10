using OpenAI.Chat;

namespace MediatorBot.Infrastructure;

public sealed class OpenAiSdkChatClient : IOpenAiChatClient
{
    private readonly ChatClient _client;

    public OpenAiSdkChatClient(OpenAiModelRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ApiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Model);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxOutputTokens);

        _client = new ChatClient(options.Model, options.ApiKey);
    }

    public async Task<OpenAiChatResponse> CompleteAsync(
        OpenAiChatRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        List<ChatMessage> messages =
        [
            new SystemChatMessage(request.SystemPrompt),
            new UserChatMessage(request.ConversationPrompt)
        ];

        var completionOptions = new ChatCompletionOptions
        {
            MaxOutputTokenCount = request.MaxOutputTokens,
            ToolChoice = ChatToolChoice.CreateRequiredChoice(),
            AllowParallelToolCalls = false,
            StoredOutputEnabled = false
        };

        foreach (var tool in request.Tools)
        {
            completionOptions.Tools.Add(ChatTool.CreateFunctionTool(
                tool.Name,
                tool.Description,
                BinaryData.FromString(tool.ParametersJson),
                functionSchemaIsStrict: true));
        }

        ChatCompletion completion = await _client.CompleteChatAsync(
            messages,
            completionOptions,
            cancellationToken);

        if (completion.FinishReason == ChatFinishReason.Length)
        {
            throw new InvalidOperationException(
                "OpenAI output ended because the output token limit was reached.");
        }

        if (completion.FinishReason == ChatFinishReason.ContentFilter)
        {
            throw new InvalidOperationException(
                "OpenAI omitted output because of the content filter.");
        }

        var toolCalls = completion.ToolCalls
            .Select(toolCall => new OpenAiToolCall(
                toolCall.FunctionName,
                toolCall.FunctionArguments.ToString()))
            .ToArray();
        var assistantText = string.Concat(
            completion.Content.Select(part => part.Text));
        var usage = new OpenAiTokenUsage(
            completion.Usage.InputTokenCount,
            completion.Usage.InputTokenDetails.CachedTokenCount,
            completion.Usage.OutputTokenCount);

        return new OpenAiChatResponse(
            toolCalls,
            assistantText,
            completion.Model,
            usage);
    }
}
