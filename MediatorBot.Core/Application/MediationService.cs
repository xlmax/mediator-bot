using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediatorBot.Core;

public sealed class MediationService
{
    private readonly IConversationStore _conversationStore;
    private readonly IModelRuntime _modelRuntime;
    private readonly IConversationContextBuilder _contextBuilder;
    private readonly ILogger<MediationService> _logger;

    public MediationService(
        IConversationStore conversationStore,
        IModelRuntime modelRuntime,
        IConversationContextBuilder contextBuilder,
        ILogger<MediationService>? logger = null)
    {
        _conversationStore = conversationStore;
        _modelRuntime = modelRuntime;
        _contextBuilder = contextBuilder;
        _logger = logger ?? NullLogger<MediationService>.Instance;
    }

    public async Task<IReadOnlyList<MediatorAction>> HandleMessageAsync(
        Guid sessionId,
        Guid participantId,
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var startedAt = Stopwatch.GetTimestamp();
        Guid? messageId = null;

        try
        {
            var session = await _conversationStore.GetSessionAsync(sessionId, cancellationToken)
                ?? throw new KeyNotFoundException($"Session '{sessionId}' was not found.");
            var author = session.GetParticipant(participantId);

            var incomingMessage = new Message(
                Guid.NewGuid(),
                session.Id,
                author.Id,
                null,
                MessageDirection.ParticipantToMediator,
                text,
                DateTimeOffset.UtcNow);
            messageId = incomingMessage.Id;

            await _conversationStore.SaveMessageAsync(incomingMessage, cancellationToken);
            var context = await _contextBuilder.BuildAsync(
                session,
                incomingMessage,
                cancellationToken);

            var result = await _modelRuntime.ProcessAsync(context, cancellationToken);
            ArgumentNullException.ThrowIfNull(result);
            ArgumentNullException.ThrowIfNull(result.Actions);

            _logger.LogInformation(
                "Handled message. SessionId={SessionId} ParticipantId={ParticipantId} " +
                "MessageId={MessageId} HistoryMessageCount={HistoryMessageCount} " +
                "DurationMs={DurationMs:F1} ResultTypes={ResultTypes} " +
                "DisclosureDecisions={DisclosureDecisions}",
                sessionId,
                participantId,
                incomingMessage.Id,
                context.History.Count,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                string.Join(',', result.Actions.Select(action => action.GetType().Name)),
                string.Join(',', result.Actions.Select(action => action.DisclosureDecision)));

            return result.Actions;
        }
        catch (Exception exception)
        {
            // Do not attach the exception: a future runtime could include private input
            // in its message. Technical identifiers and the exception type are sufficient here.
            _logger.LogError(
                "Failed to handle message. SessionId={SessionId} ParticipantId={ParticipantId} " +
                "MessageId={MessageId} DurationMs={DurationMs:F1} ErrorType={ErrorType}",
                sessionId,
                participantId,
                messageId,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                exception.GetType().Name);
            throw;
        }
    }

}
