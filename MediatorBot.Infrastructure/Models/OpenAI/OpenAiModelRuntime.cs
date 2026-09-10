using System.Diagnostics;
using MediatorBot.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediatorBot.Infrastructure;

public sealed class OpenAiModelRuntime : IModelRuntime
{
    private readonly IOpenAiChatClient _chatClient;
    private readonly OpenAiConversationPromptBuilder _promptBuilder;
    private readonly OpenAiToolCallMapper _toolCallMapper;
    private readonly OpenAiModelRuntimeOptions _options;
    private readonly ILogger<OpenAiModelRuntime> _logger;

    public OpenAiModelRuntime(
        IOpenAiChatClient chatClient,
        OpenAiConversationPromptBuilder promptBuilder,
        OpenAiToolCallMapper toolCallMapper,
        OpenAiModelRuntimeOptions options,
        ILogger<OpenAiModelRuntime>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Model);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxOutputTokens);

        _chatClient = chatClient;
        _promptBuilder = promptBuilder;
        _toolCallMapper = toolCallMapper;
        _options = options;
        _logger = logger ?? NullLogger<OpenAiModelRuntime>.Instance;
    }

    public async Task<ModelResult> ProcessAsync(
        ConversationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var startedAt = Stopwatch.GetTimestamp();

        try
        {
            var request = new OpenAiChatRequest(
                OpenAiConversationPromptBuilder.SystemPrompt,
                _promptBuilder.Build(context),
                OpenAiToolCatalog.All,
                _options.MaxOutputTokens);
            var response = await _chatClient.CompleteAsync(request, cancellationToken);

            IReadOnlyList<MediatorAction> actions;
            var resultType = "ToolCalls";
            if (response.ToolCalls.Count > 0)
            {
                actions = _toolCallMapper.Map(context.Session, response.ToolCalls);
            }
            else if (!string.IsNullOrWhiteSpace(response.AssistantText))
            {
                actions =
                [
                    new SendToParticipant(
                        context.Author.Id,
                        response.AssistantText.Trim())
                ];
                resultType = "AssistantTextFallback";
            }
            else
            {
                actions = [new NoAction()];
                resultType = "EmptyResponseFallback";
            }

            _logger.LogInformation(
                "OpenAI request completed. SessionId={SessionId} Model={Model} " +
                "DurationMs={DurationMs:F1} InputTokens={InputTokens} " +
                "CachedInputTokens={CachedInputTokens} OutputTokens={OutputTokens} " +
                "ResultType={ResultType} Tools={Tools}",
                context.Session.Id,
                response.Model,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                response.Usage.InputTokens,
                response.Usage.CachedInputTokens,
                response.Usage.OutputTokens,
                resultType,
                string.Join(',', response.ToolCalls.Select(toolCall => toolCall.Name)));

            return new ModelResult(actions);
        }
        catch (Exception exception)
        {
            // Do not attach the exception: provider errors can contain request fragments.
            _logger.LogError(
                "OpenAI request failed. SessionId={SessionId} Model={Model} " +
                "DurationMs={DurationMs:F1} ErrorType={ErrorType}",
                context.Session.Id,
                _options.Model,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                exception.GetType().Name);
            throw;
        }
    }
}
