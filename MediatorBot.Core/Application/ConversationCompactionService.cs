using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediatorBot.Core;

public sealed class ConversationCompactionService : IConversationCompactionService
{
    private readonly IConversationStore _conversationStore;
    private readonly IConversationCompactionStore _compactionStore;
    private readonly IConversationSummaryGenerator _summaryGenerator;
    private readonly ConversationCompactionOptions _options;
    private readonly ILogger<ConversationCompactionService> _logger;

    public ConversationCompactionService(
        IConversationStore conversationStore,
        IConversationCompactionStore compactionStore,
        IConversationSummaryGenerator summaryGenerator,
        ConversationCompactionOptions options,
        ILogger<ConversationCompactionService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(conversationStore);
        ArgumentNullException.ThrowIfNull(compactionStore);
        ArgumentNullException.ThrowIfNull(summaryGenerator);
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);

        _conversationStore = conversationStore;
        _compactionStore = compactionStore;
        _summaryGenerator = summaryGenerator;
        _options = options;
        _logger = logger ?? NullLogger<ConversationCompactionService>.Instance;
    }

    public async Task<ConversationCompactionPlan?> PrepareAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return null;
        }

        var batch = await _compactionStore.GetCompactionBatchAsync(
            sessionId,
            _options.TriggerMessageCount,
            _options.TriggerHistoryCharacters,
            _options.RetainRecentMessageCount,
            _options.RetainRecentCharacters,
            cancellationToken);
        if (batch is null)
        {
            return null;
        }

        var session = await _conversationStore.GetSessionAsync(sessionId, cancellationToken)
            ?? throw new KeyNotFoundException($"Session '{sessionId}' was not found.");
        return new ConversationCompactionPlan(session, batch);
    }

    public async Task<ConversationCompactionResult> ExecuteAsync(
        ConversationCompactionPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Session.Id != plan.Batch.SessionId || plan.Batch.Messages.Count == 0)
        {
            throw new ArgumentException(
                "The compaction plan must contain messages from its session.",
                nameof(plan));
        }

        var startedAt = Stopwatch.GetTimestamp();
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutCancellation.CancelAfter(_options.OperationTimeout);

        var summary = await _summaryGenerator.GenerateAsync(
            plan.Session,
            plan.Batch.PreviousSummary,
            plan.Batch.Messages,
            _options.MaxSummaryCharacters,
            timeoutCancellation.Token);
        ArgumentNullException.ThrowIfNull(summary);
        ValidateSummary(summary, _options.MaxSummaryCharacters);

        await _compactionStore.CommitCompactionAsync(
            plan.Session.Id,
            plan.Batch.ExpectedSummaryVersion,
            plan.Batch.CompactedThroughSequence,
            summary,
            DateTimeOffset.UtcNow,
            timeoutCancellation.Token);

        _logger.LogInformation(
            "Conversation history compacted. SessionId={SessionId} " +
            "CompactedMessageCount={CompactedMessageCount} " +
            "CompactedThroughSequence={CompactedThroughSequence} " +
            "SummaryCharacterCount={SummaryCharacterCount} DurationMs={DurationMs:F1}",
            plan.Session.Id,
            plan.Batch.Messages.Count,
            plan.Batch.CompactedThroughSequence,
            summary.CharacterCount,
            Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);

        return new ConversationCompactionResult(
            plan.Batch.Messages.Count,
            plan.Batch.CompactedThroughSequence,
            summary.CharacterCount);
    }

    private static void ValidateOptions(ConversationCompactionOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.TriggerMessageCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.TriggerHistoryCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.RetainRecentMessageCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.RetainRecentCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxSummaryCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxOutputTokens);
        if (options.RetainRecentMessageCount >= options.TriggerMessageCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.RetainRecentMessageCount),
                "Retained message count must be below the compaction trigger.");
        }

        if (options.RetainRecentCharacters >= options.TriggerHistoryCharacters)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.RetainRecentCharacters),
                "Retained character count must be below the compaction trigger.");
        }

        if (options.OperationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options.OperationTimeout));
        }

        if (options.RetryDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options.RetryDelay));
        }
    }

    private static void ValidateSummary(
        ConversationSummaryContent summary,
        int maxSummaryCharacters)
    {
        ArgumentNullException.ThrowIfNull(summary.PrivateContextFromParticipantA);
        ArgumentNullException.ThrowIfNull(summary.PrivateContextFromParticipantB);
        ArgumentNullException.ThrowIfNull(summary.SharedContextAndAgreements);
        ArgumentNullException.ThrowIfNull(summary.BoundariesAndSafety);
        if (summary.CharacterCount > maxSummaryCharacters)
        {
            throw new InvalidDataException(
                $"Compacted summary exceeds the {maxSummaryCharacters}-character limit.");
        }
    }
}
