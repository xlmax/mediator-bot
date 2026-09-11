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

            if (response.ToolCalls.Count == 0)
            {
                var reason = string.IsNullOrWhiteSpace(response.AssistantText)
                    ? OpenAiProtocolFailureReason.MissingToolCall
                    : OpenAiProtocolFailureReason.UnexpectedAssistantText;
                throw new OpenAiProtocolException(
                    reason,
                    "OpenAI returned no mediator tool call.");
            }

            if (response.ToolCalls.Count > 1)
            {
                throw new OpenAiProtocolException(
                    OpenAiProtocolFailureReason.MultipleToolCalls,
                    "OpenAI returned more than one mediator tool call.");
            }

            IReadOnlyList<MediatorAction> actions;
            try
            {
                actions = _toolCallMapper.Map(context, response.ToolCalls);
            }
            catch (Exception exception) when (
                exception is InvalidDataException or
                InvalidOperationException or
                ArgumentException)
            {
                throw new OpenAiProtocolException(
                    OpenAiProtocolFailureReason.InvalidToolCall,
                    "OpenAI returned an invalid mediator tool call.",
                    exception);
            }

            _logger.LogInformation(
                "OpenAI request completed. SessionId={SessionId} Model={Model} " +
                "DurationMs={DurationMs:F1} InputTokens={InputTokens} " +
                "CachedInputTokens={CachedInputTokens} OutputTokens={OutputTokens} " +
                "ResultType={ResultType} Tools={Tools} " +
                "DisclosureDecisions={DisclosureDecisions}",
                context.Session.Id,
                response.Model,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                response.Usage.InputTokens,
                response.Usage.CachedInputTokens,
                response.Usage.OutputTokens,
                "ToolCalls",
                string.Join(',', response.ToolCalls.Select(toolCall => toolCall.Name)),
                string.Join(',', actions.Select(action => action.DisclosureDecision)));

            return new ModelResult(actions);
        }
        catch (OpenAiProviderException exception)
        {
            _logger.LogError(
                "OpenAI-compatible provider failed. SessionId={SessionId} " +
                "Model={Model} DurationMs={DurationMs:F1} ProviderCode={ProviderCode} " +
                "ProviderErrorType={ProviderErrorType} ProviderName={ProviderName} " +
                "ProviderParameter={ProviderParameter} ErrorType={ErrorType}",
                context.Session.Id,
                _options.Model,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                exception.ProviderCode,
                exception.ProviderErrorType,
                exception.ProviderName,
                exception.ProviderParameter,
                exception.GetType().Name);
            throw;
        }
        catch (OpenAiProtocolException exception)
        {
            _logger.LogError(
                "OpenAI protocol validation failed. SessionId={SessionId} " +
                "Model={Model} DurationMs={DurationMs:F1} " +
                "ProtocolReason={ProtocolReason} ErrorType={ErrorType}",
                context.Session.Id,
                _options.Model,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                exception.Reason,
                exception.GetType().Name);
            throw;
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
