using System.Diagnostics;
using System.Globalization;
using MediatorBot.Core;
using MediatorBot.Infrastructure;
using Microsoft.Extensions.Logging;

namespace MediatorBot.Telegram;

public sealed class TelegramQueuedTurnProcessor(
    TelegramParticipantRegistry participantRegistry,
    MediationService mediationService,
    TelegramMediatorActionDispatcher actionDispatcher,
    TelegramAdapterOptions options,
    ILogger<TelegramQueuedTurnProcessor> logger) : ITelegramQueuedTurnProcessor
{
    public async Task<TelegramMessageProcessingStatus> ProcessAsync(
        PendingExternalTurn turn,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(turn);
        var telegramUserId = long.Parse(
            turn.ExternalUserId,
            CultureInfo.InvariantCulture);
        var updateId = turn.SourceSequence;
        var binding = await participantRegistry.FindByTelegramUserIdAsync(
            telegramUserId,
            cancellationToken);
        if (binding is null ||
            binding.Session.Id != turn.SessionId ||
            binding.Participant.Id != turn.ParticipantId)
        {
            throw new InvalidOperationException(
                $"Pending Telegram turn '{turn.Id}' does not match configured participants.");
        }

        var turnId = Guid.NewGuid();
        var startedAt = Stopwatch.GetTimestamp();
        using var scope = logger.BeginScope("MediatorTurn {TurnId}", turnId);

        logger.LogInformation(
            "Turn started. UpdateId={UpdateId} TelegramUserId={TelegramUserId} " +
            "ParticipantId={ParticipantId} SessionId={SessionId} Model={Model}",
            updateId,
            telegramUserId,
            binding.Participant.Id,
            binding.Session.Id,
            options.ModelDisplayName);

        try
        {
            var actions = await mediationService.HandleMessageAsync(
                binding.Session.Id,
                binding.Participant.Id,
                turn.Text,
                cancellationToken);
            await actionDispatcher.DispatchAsync(
                updateId,
                telegramUserId,
                binding.Session,
                actions,
                cancellationToken);

            logger.LogInformation(
                "Turn completed. UpdateId={UpdateId} " +
                "TelegramUserId={TelegramUserId} ParticipantId={ParticipantId} " +
                "SessionId={SessionId} DurationMs={DurationMs:F1} " +
                "ResultTypes={ResultTypes}",
                updateId,
                telegramUserId,
                binding.Participant.Id,
                binding.Session.Id,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                string.Join(',', actions.Select(action => action.GetType().Name)));
            return TelegramMessageProcessingStatus.Processed;
        }
        catch (OpenAiProviderException exception)
        {
            logger.LogWarning(
                "Turn failed because the model provider is unavailable. " +
                "UpdateId={UpdateId} TelegramUserId={TelegramUserId} " +
                "ParticipantId={ParticipantId} SessionId={SessionId} " +
                "DurationMs={DurationMs:F1} ProviderCode={ProviderCode} " +
                "ErrorType={ErrorType}",
                updateId,
                telegramUserId,
                binding.Participant.Id,
                binding.Session.Id,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                exception.ProviderCode,
                exception.GetType().Name);
            return TelegramMessageProcessingStatus.ModelUnavailable;
        }
        catch (OpenAiProtocolException exception)
        {
            logger.LogError(
                "Turn failed because of an OpenAI protocol error. " +
                "UpdateId={UpdateId} TelegramUserId={TelegramUserId} " +
                "ParticipantId={ParticipantId} SessionId={SessionId} " +
                "DurationMs={DurationMs:F1} ProtocolReason={ProtocolReason} " +
                "ErrorType={ErrorType}",
                updateId,
                telegramUserId,
                binding.Participant.Id,
                binding.Session.Id,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                exception.Reason,
                nameof(OpenAiProtocolException));
            return TelegramMessageProcessingStatus.ModelProtocolFailure;
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Turn failed. UpdateId={UpdateId} TelegramUserId={TelegramUserId} " +
                "ParticipantId={ParticipantId} SessionId={SessionId} " +
                "DurationMs={DurationMs:F1} ErrorType={ErrorType}",
                updateId,
                telegramUserId,
                binding.Participant.Id,
                binding.Session.Id,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                exception.GetType().Name);
            throw;
        }
    }
}
