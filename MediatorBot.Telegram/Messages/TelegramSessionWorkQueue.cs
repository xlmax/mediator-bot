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
    TelegramTurnProcessingOptions turnProcessingOptions,
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
    private const string TurnFailedMessage =
        "Не удалось обработать сохранённое сообщение. " +
        "Следующие сообщения продолжат обрабатываться. " +
        "Повторить попытку можно командой /retry_failed.";

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
                return TelegramMessageProcessingStatus.Deferred;
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

        try
        {
            return await completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            lock (_lock)
            {
                if (state.Waiters.TryGetValue(turn.Id, out var current) &&
                    ReferenceEquals(current, completion))
                {
                    state.Waiters.Remove(turn.Id);
                }
            }
        }
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
        TaskCompletionSource<TelegramMessageProcessingStatus>[] waiters;
        lock (_lock)
        {
            _stopping = true;
            _lifetimeCancellation.Cancel();
            runners = _activeRunners
                .Select(completion => completion.Task)
                .ToArray();
            waiters = _states.Values
                .SelectMany(state => state.Waiters
                    .Where(waiter => waiter.Key != state.ActiveTurnId)
                    .Select(waiter => waiter.Value))
                .ToArray();
            foreach (var state in _states.Values)
            {
                foreach (var turnId in state.Waiters.Keys
                             .Where(turnId => turnId != state.ActiveTurnId)
                             .ToArray())
                {
                    state.Waiters.Remove(turnId);
                }
            }
        }

        foreach (var waiter in waiters)
        {
            waiter.TrySetResult(TelegramMessageProcessingStatus.Deferred);
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
                DeferWaiters(state);
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

            SetActiveTurn(state, turn.Id);
            int attemptCount;
            try
            {
                attemptCount = await turnQueueStore.BeginAttemptAsync(
                    sessionId,
                    turn.Id,
                    DateTimeOffset.UtcNow);
            }
            catch (Exception exception)
            {
                logger.LogError(
                    "Pending turn attempt checkpoint failed. " +
                    "SessionId={SessionId} UpdateId={UpdateId} ErrorType={ErrorType}",
                    sessionId,
                    turn.SourceSequence,
                    exception.GetType().Name);
                ClearActiveTurn(state, turn.Id);
                DeferWaiters(state);
                StopRunner(state);
                ScheduleRecovery(sessionId);
                return;
            }

            if (attemptCount > turnProcessingOptions.MaxAttempts)
            {
                if (await TryQuarantineTurnAsync(
                        state,
                        turn,
                        "RetryLimitExceeded"))
                {
                    ClearActiveTurn(state, turn.Id);
                    continue;
                }

                ClearActiveTurn(state, turn.Id);
                StopRunner(state);
                ScheduleRecovery(sessionId);
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
                ClearActiveTurn(state, turn.Id);
                if (IsStopping())
                {
                    StopRunner(state);
                    return;
                }
            }
            catch (Exception exception)
            {
                logger.LogError(
                    "Queued Telegram turn processing failed. " +
                    "SessionId={SessionId} UpdateId={UpdateId} " +
                    "AttemptCount={AttemptCount} ErrorType={ErrorType}",
                    sessionId,
                    turn.SourceSequence,
                    attemptCount,
                    exception.GetType().Name);
                if (attemptCount >= turnProcessingOptions.MaxAttempts)
                {
                    if (await TryQuarantineTurnAsync(
                            state,
                            turn,
                            exception.GetType().Name))
                    {
                        ClearActiveTurn(state, turn.Id);
                        continue;
                    }
                }

                ClearActiveTurn(state, turn.Id);
                DeferWaiters(state);
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

    private void SetActiveTurn(SessionQueueState state, Guid turnId)
    {
        lock (_lock)
        {
            state.ActiveTurnId = turnId;
        }
    }

    private void ClearActiveTurn(SessionQueueState state, Guid turnId)
    {
        lock (_lock)
        {
            if (state.ActiveTurnId == turnId)
            {
                state.ActiveTurnId = null;
            }
        }
    }

    private bool CompleteWaiter(
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

        return waiter?.TrySetResult(result) == true;
    }

    private void DeferWaiters(SessionQueueState state)
    {
        TaskCompletionSource<TelegramMessageProcessingStatus>[] waiters;
        lock (_lock)
        {
            waiters = state.Waiters.Values.ToArray();
            state.Waiters.Clear();
        }

        foreach (var waiter in waiters)
        {
            waiter.TrySetResult(TelegramMessageProcessingStatus.Deferred);
        }
    }

    private async Task<bool> TryQuarantineTurnAsync(
        SessionQueueState state,
        PendingExternalTurn turn,
        string failureType)
    {
        try
        {
            await turnQueueStore.MarkFailedAsync(
                turn.SessionId,
                turn.Id,
                failureType,
                DateTimeOffset.UtcNow);
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Failed turn quarantine checkpoint failed. " +
                "SessionId={SessionId} UpdateId={UpdateId} ErrorType={ErrorType}",
                turn.SessionId,
                turn.SourceSequence,
                exception.GetType().Name);
            return false;
        }

        logger.LogError(
            "Queued Telegram turn was quarantined after exhausting retries. " +
            "SessionId={SessionId} UpdateId={UpdateId} AttemptCount={AttemptCount} " +
            "FailureType={FailureType}",
            turn.SessionId,
            turn.SourceSequence,
            turnProcessingOptions.MaxAttempts,
            failureType);
        var waiterCompleted = CompleteWaiter(
            state,
            turn.Id,
            TelegramMessageProcessingStatus.ProcessingFailed);
        DeferWaiters(state);
        if (!waiterCompleted)
        {
            var telegramUserId = long.Parse(
                turn.ExternalUserId,
                CultureInfo.InvariantCulture);
            await SafeSendAsync(telegramUserId, TurnFailedMessage);
        }

        return true;
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
                turnProcessingOptions.RetryDelay,
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

        public Guid? ActiveTurnId { get; set; }

        public TaskCompletionSource? RunnerCompletion { get; set; }

        public bool IsCompacting { get; set; }

        public DateTimeOffset? RetryAfter { get; set; }

        public long WakeVersion { get; set; }
    }
}
