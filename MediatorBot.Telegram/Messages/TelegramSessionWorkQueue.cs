using System.Globalization;
using MediatorBot.Core;
using Microsoft.Extensions.Logging;

namespace MediatorBot.Telegram;

public sealed class TelegramSessionWorkQueue(
    ISessionTurnCoordinator turnCoordinator,
    IConversationCompactionService compactionService,
    IExternalTurnQueueStore turnQueueStore,
    ITelegramQueuedTurnProcessor turnProcessor,
    ITelegramMessageTransport transport,
    TelegramAdapterOptions telegramOptions,
    ConversationCompactionOptions compactionOptions,
    ILogger<TelegramSessionWorkQueue> logger)
{
    private const string CompactionStartedMessage =
        "Я сейчас упорядочиваю накопившийся контекст и отвечу чуть позже. " +
        "Ваше сообщение сохранено.";
    private const string CompactionCompletedMessage =
        "Готово, продолжаю с вашим сообщением.";
    private const string CompactionFailedMessage =
        "Не удалось полностью обновить краткую память, но ваше сообщение сохранено. " +
        "Продолжаю без удаления прежней истории.";

    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, SessionQueueState> _states = [];
    private readonly HashSet<TaskCompletionSource> _activeRunners = [];
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private bool _stopping;

    public async Task<TelegramMessageProcessingStatus> ProcessEnqueuedAsync(
        PendingExternalTurn turn,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(turn);
        var completion = new TaskCompletionSource<TelegramMessageProcessingStatus>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        SessionQueueState state;
        TaskCompletionSource? runnerCompletion = null;
        var notifyDuringCompaction = false;

        lock (_lock)
        {
            if (_stopping)
            {
                throw new OperationCanceledException(
                    "Telegram turn processing is stopping. The durable turn remains pending.");
            }

            state = GetOrCreateState(turn.SessionId);
            state.WakeVersion++;
            state.Waiters.Add(turn.Id, completion);
            if (!state.IsRunning)
            {
                runnerCompletion = PrepareRunner(state);
            }

            notifyDuringCompaction = state.IsCompacting;
        }

        if (notifyDuringCompaction)
        {
            EnsureCompactionNotification(state, turn);
        }

        if (runnerCompletion is not null)
        {
            StartRunner(turn.SessionId, state, runnerCompletion);
        }

        return await completion.Task.WaitAsync(cancellationToken);
    }

    public Task RecoverSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SessionQueueState state;
        TaskCompletionSource? runnerCompletion = null;
        lock (_lock)
        {
            if (_stopping)
            {
                return Task.CompletedTask;
            }

            state = GetOrCreateState(sessionId);
            state.WakeVersion++;
            if (!state.IsRunning)
            {
                runnerCompletion = PrepareRunner(state);
            }
        }

        if (runnerCompletion is not null)
        {
            StartRunner(sessionId, state, runnerCompletion);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task[] runners;
        lock (_lock)
        {
            _stopping = true;
            _lifetimeCancellation.Cancel();
            runners = _activeRunners
                .Select(completion => completion.Task)
                .ToArray();
        }

        await Task.WhenAll(runners).WaitAsync(cancellationToken);
    }

    private TaskCompletionSource PrepareRunner(SessionQueueState state)
    {
        state.IsRunning = true;
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        state.RunnerCompletion = completion;
        _activeRunners.Add(completion);
        return completion;
    }

    private void StartRunner(
        Guid sessionId,
        SessionQueueState state,
        TaskCompletionSource completion) =>
        _ = RunSessionTrackedAsync(sessionId, state, completion);

    private async Task RunSessionTrackedAsync(
        Guid sessionId,
        SessionQueueState state,
        TaskCompletionSource completion)
    {
        try
        {
            await RunSessionAsync(sessionId, state);
        }
        finally
        {
            lock (_lock)
            {
                _activeRunners.Remove(completion);
                if (ReferenceEquals(state.RunnerCompletion, completion))
                {
                    state.RunnerCompletion = null;
                }
            }

            completion.TrySetResult();
        }
    }

    private async Task RunSessionAsync(Guid sessionId, SessionQueueState state)
    {
        while (true)
        {
            var plan = await TryPrepareCompactionAsync(sessionId, state);
            if (plan is not null)
            {
                await RunCompactionAsync(state, plan);
            }

            if (IsStopping())
            {
                StopRunner(state);
                return;
            }

            long observedWakeVersion;
            lock (_lock)
            {
                observedWakeVersion = state.WakeVersion;
            }

            PendingExternalTurn? turn;
            try
            {
                turn = (await turnQueueStore.GetPendingAsync(sessionId))
                    .FirstOrDefault();
            }
            catch (Exception exception)
            {
                logger.LogError(
                    "Pending turn query failed. SessionId={SessionId} ErrorType={ErrorType}",
                    sessionId,
                    exception.GetType().Name);
                StopRunner(state);
                ScheduleRecovery(sessionId);
                return;
            }

            if (turn is null)
            {
                lock (_lock)
                {
                    if (state.WakeVersion != observedWakeVersion)
                    {
                        continue;
                    }

                    state.IsRunning = false;
                }

                return;
            }

            try
            {
                var result = await turnCoordinator.ExecuteAsync(
                    sessionId,
                    cancellationToken => turnProcessor.ProcessAsync(turn, cancellationToken),
                    CancellationToken.None);
                await turnQueueStore.CompleteAsync(sessionId, turn.Id);
                CompleteWaiter(state, turn.Id, result);
                if (IsStopping())
                {
                    StopRunner(state);
                    return;
                }
            }
            catch (Exception exception)
            {
                logger.LogError(
                    "Queued Telegram turn remains pending after processing failure. " +
                    "SessionId={SessionId} UpdateId={UpdateId} ErrorType={ErrorType}",
                    sessionId,
                    turn.SourceSequence,
                    exception.GetType().Name);
                FailWaiter(state, turn.Id, exception);
                StopRunner(state);
                ScheduleRecovery(sessionId);
                return;
            }
        }
    }

    private async Task<ConversationCompactionPlan?> TryPrepareCompactionAsync(
        Guid sessionId,
        SessionQueueState state)
    {
        if (!compactionOptions.Enabled)
        {
            return null;
        }

        lock (_lock)
        {
            if (state.RetryAfter > DateTimeOffset.UtcNow)
            {
                return null;
            }
        }

        try
        {
            return await turnCoordinator.ExecuteAsync(
                sessionId,
                cancellationToken => compactionService.PrepareAsync(
                    sessionId,
                    cancellationToken),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            lock (_lock)
            {
                state.RetryAfter = DateTimeOffset.UtcNow + compactionOptions.RetryDelay;
            }

            logger.LogWarning(
                "Conversation compaction preparation failed. " +
                "SessionId={SessionId} ErrorType={ErrorType}",
                sessionId,
                exception.GetType().Name);
            return null;
        }
    }

    private async Task RunCompactionAsync(
        SessionQueueState state,
        ConversationCompactionPlan plan)
    {
        lock (_lock)
        {
            state.IsCompacting = true;
        }

        try
        {
            var pending = await turnQueueStore.GetPendingAsync(plan.Session.Id);
            foreach (var turn in pending)
            {
                EnsureCompactionNotification(state, turn);
            }

            using var timeout = new CancellationTokenSource(compactionOptions.OperationTimeout);
            var result = await turnCoordinator.ExecuteAsync(
                plan.Session.Id,
                cancellationToken => compactionService.ExecuteAsync(plan, cancellationToken),
                timeout.Token);
            lock (_lock)
            {
                state.RetryAfter = null;
            }

            logger.LogInformation(
                "Conversation compaction committed. SessionId={SessionId} " +
                "CompactedMessageCount={CompactedMessageCount} " +
                "SummaryCharacterCount={SummaryCharacterCount}",
                plan.Session.Id,
                result.CompactedMessageCount,
                result.SummaryCharacterCount);
            await FinishCompactionNotificationsAsync(state, CompactionCompletedMessage);
        }
        catch (Exception exception)
        {
            lock (_lock)
            {
                state.RetryAfter = DateTimeOffset.UtcNow + compactionOptions.RetryDelay;
            }

            logger.LogWarning(
                "Conversation compaction failed without deleting history. " +
                "SessionId={SessionId} ErrorType={ErrorType}",
                plan.Session.Id,
                exception.GetType().Name);
            await FinishCompactionNotificationsAsync(state, CompactionFailedMessage);
        }
    }

    private void EnsureCompactionNotification(
        SessionQueueState state,
        PendingExternalTurn turn)
    {
        var telegramUserId = long.Parse(turn.ExternalUserId, CultureInfo.InvariantCulture);
        lock (_lock)
        {
            if (!state.IsCompacting ||
                !state.NotifiedParticipants.Add(telegramUserId))
            {
                return;
            }

            state.StartNotificationTasks[telegramUserId] = SafeSendAsync(
                telegramUserId,
                CompactionStartedMessage);
        }
    }

    private async Task FinishCompactionNotificationsAsync(
        SessionQueueState state,
        string finalMessage)
    {
        KeyValuePair<long, Task>[] starts;
        lock (_lock)
        {
            starts = state.StartNotificationTasks.ToArray();
            state.StartNotificationTasks.Clear();
            state.NotifiedParticipants.Clear();
            state.IsCompacting = false;
        }

        await Task.WhenAll(starts.Select(entry => entry.Value));
        await Task.WhenAll(starts.Select(entry =>
            SafeSendAsync(entry.Key, finalMessage)));
    }

    private async Task SafeSendAsync(long telegramUserId, string text)
    {
        try
        {
            using var timeout = new CancellationTokenSource(telegramOptions.DeliveryTimeout);
            await transport.SendTextMessageAsync(telegramUserId, text, timeout.Token);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Telegram maintenance notification failed. " +
                "TelegramUserId={TelegramUserId} ErrorType={ErrorType}",
                telegramUserId,
                exception.GetType().Name);
        }
    }

    private SessionQueueState GetOrCreateState(Guid sessionId)
    {
        if (!_states.TryGetValue(sessionId, out var state))
        {
            state = new SessionQueueState();
            _states.Add(sessionId, state);
        }

        return state;
    }

    private void CompleteWaiter(
        SessionQueueState state,
        Guid turnId,
        TelegramMessageProcessingStatus result)
    {
        TaskCompletionSource<TelegramMessageProcessingStatus>? waiter = null;
        lock (_lock)
        {
            if (state.Waiters.Remove(turnId, out var found))
            {
                waiter = found;
            }
        }

        waiter?.TrySetResult(result);
    }

    private void FailWaiter(
        SessionQueueState state,
        Guid turnId,
        Exception exception)
    {
        TaskCompletionSource<TelegramMessageProcessingStatus>? waiter = null;
        lock (_lock)
        {
            if (state.Waiters.Remove(turnId, out var found))
            {
                waiter = found;
            }
        }

        waiter?.TrySetException(exception);
    }

    private void ScheduleRecovery(Guid sessionId)
    {
        lock (_lock)
        {
            if (_stopping)
            {
                return;
            }
        }

        _ = RecoverAfterDelayAsync(sessionId);
    }

    private async Task RecoverAfterDelayAsync(Guid sessionId)
    {
        try
        {
            await Task.Delay(
                compactionOptions.RetryDelay,
                _lifetimeCancellation.Token);
            await RecoverSessionAsync(sessionId, _lifetimeCancellation.Token);
        }
        catch (OperationCanceledException)
            when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
    }

    private bool IsStopping()
    {
        lock (_lock)
        {
            return _stopping;
        }
    }

    private void StopRunner(SessionQueueState state)
    {
        lock (_lock)
        {
            state.IsRunning = false;
        }
    }

    private sealed class SessionQueueState
    {
        public Dictionary<Guid, TaskCompletionSource<TelegramMessageProcessingStatus>> Waiters
        {
            get;
        } = [];

        public HashSet<long> NotifiedParticipants { get; } = [];

        public Dictionary<long, Task> StartNotificationTasks { get; } = [];

        public bool IsRunning { get; set; }

        public TaskCompletionSource? RunnerCompletion { get; set; }

        public bool IsCompacting { get; set; }

        public DateTimeOffset? RetryAfter { get; set; }

        public long WakeVersion { get; set; }
    }
}
