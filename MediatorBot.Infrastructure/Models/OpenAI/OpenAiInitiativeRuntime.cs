using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediatorBot.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediatorBot.Infrastructure;

public sealed class OpenAiInitiativeRuntime(
    IOpenAiChatClient chatClient,
    OpenAiInitiativePromptBuilder promptBuilder,
    OpenAiModelRuntimeOptions options,
    InitiativeOptions initiativeOptions,
    ILogger<OpenAiInitiativeRuntime>? logger = null) : IInitiativeRuntime
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ILogger<OpenAiInitiativeRuntime> _logger =
        logger ?? NullLogger<OpenAiInitiativeRuntime>.Instance;

    public async Task<InitiativeProposal> EvaluateAsync(
        InitiativeContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            var response = await chatClient.CompleteAsync(
                new OpenAiChatRequest(
                    OpenAiInitiativePromptBuilder.SystemPrompt,
                    promptBuilder.Build(context),
                    OpenAiInitiativeToolCatalog.All,
                    options.MaxOutputTokens),
                cancellationToken);
            if (response.ToolCalls.Count != 1 ||
                response.ToolCalls[0].Name != OpenAiInitiativeToolCatalog.RecordDecision ||
                !string.IsNullOrWhiteSpace(response.AssistantText))
            {
                throw new OpenAiProtocolException(
                    response.ToolCalls.Count > 1
                        ? OpenAiProtocolFailureReason.MultipleToolCalls
                        : OpenAiProtocolFailureReason.InvalidToolCall,
                    "OpenAI returned an invalid initiative decision response.");
            }

            InitiativeArguments arguments;
            try
            {
                arguments = JsonSerializer.Deserialize<InitiativeArguments>(
                    response.ToolCalls[0].ArgumentsJson,
                    JsonOptions) ?? throw new InvalidDataException(
                    "Initiative decision arguments are empty.");
            }
            catch (Exception exception) when (
                exception is JsonException or InvalidDataException)
            {
                throw new OpenAiProtocolException(
                    OpenAiProtocolFailureReason.InvalidToolCall,
                    "OpenAI returned malformed initiative decision arguments.",
                    exception);
            }

            var proposal = Map(context, arguments);
            _logger.LogInformation(
                "OpenAI initiative evaluation completed. SessionId={SessionId} " +
                "Model={Model} DurationMs={DurationMs:F1} InputTokens={InputTokens} " +
                "CachedInputTokens={CachedInputTokens} OutputTokens={OutputTokens} " +
                "Decision={Decision} Intent={Intent} Phase={Phase}",
                context.Session.Id,
                response.Model,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                response.Usage.InputTokens,
                response.Usage.CachedInputTokens,
                response.Usage.OutputTokens,
                proposal.DecisionKind,
                proposal.Intent,
                proposal.Phase);
            return proposal;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                "OpenAI initiative evaluation failed. SessionId={SessionId} " +
                "Model={Model} DurationMs={DurationMs:F1} ErrorType={ErrorType}",
                context.Session.Id,
                options.Model,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                exception.GetType().Name);
            throw;
        }
    }

    private InitiativeProposal Map(
        InitiativeContext context,
        InitiativeArguments arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments.OperationalRationale) ||
            arguments.ReevaluateAfterMinutes <
                initiativeOptions.MinimumReevaluationMinutes ||
            arguments.ReevaluateAfterMinutes >
                initiativeOptions.MaximumReevaluationMinutes)
        {
            throw new OpenAiProtocolException(
                OpenAiProtocolFailureReason.InvalidToolCall,
                "Initiative decision contains invalid rationale or timing.");
        }

        var target = ParseParticipantId(
            context.Session,
            arguments.TargetParticipantId,
            nameof(arguments.TargetParticipantId));
        var pauseTarget = ParseParticipantId(
            context.Session,
            arguments.PauseParticipantId,
            nameof(arguments.PauseParticipantId));
        if (arguments.PauseForMinutes is int pauseMinutes &&
            (pauseMinutes < initiativeOptions.MinimumReevaluationMinutes ||
             pauseMinutes > initiativeOptions.MaximumReevaluationMinutes))
        {
            throw new OpenAiProtocolException(
                OpenAiProtocolFailureReason.InvalidToolCall,
                "Initiative contact pause has invalid timing.");
        }

        return new InitiativeProposal(
            arguments.Phase,
            arguments.Confidence,
            arguments.DecisionKind,
            target,
            arguments.Intent,
            arguments.ReasonCode,
            arguments.OperationalRationale,
            NullIfWhiteSpace(arguments.TextForParticipantA),
            NullIfWhiteSpace(arguments.TextForParticipantB),
            arguments.ReevaluateAfterMinutes,
            pauseTarget,
            arguments.PauseForMinutes);
    }

    private static Guid? ParseParticipantId(
        Session session,
        string? value,
        string propertyName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!Guid.TryParse(value, out var participantId) ||
            (participantId != session.ParticipantA.Id &&
             participantId != session.ParticipantB.Id))
        {
            throw new OpenAiProtocolException(
                OpenAiProtocolFailureReason.InvalidToolCall,
                $"Initiative property '{propertyName}' is not a session participant.");
        }

        return participantId;
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed record InitiativeArguments(
        RelationshipPhase Phase,
        InitiativeConfidence Confidence,
        InitiativeDecisionKind DecisionKind,
        string? TargetParticipantId,
        InitiativeIntent Intent,
        InitiativeReasonCode ReasonCode,
        string OperationalRationale,
        string? TextForParticipantA,
        string? TextForParticipantB,
        int ReevaluateAfterMinutes,
        string? PauseParticipantId,
        int? PauseForMinutes);
}
