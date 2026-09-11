using System.Collections.Concurrent;
using System.Globalization;
using MediatorBot.Core;
using MediatorBot.Infrastructure;
using MediatorBot.Telegram;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediatorBot.Tests;

public sealed class TelegramSessionWorkQueueTests
{
    [Fact]
    public async Task MessagesArrivingDuringCompaction_AreNotifiedAndProcessedByUpdateId()
    {
        var session = CreateSession();
        var compaction = new BlockingCompactionService(session);
        var transport = new ConcurrentRecordingTransport();
        var fixture = CreateQueue(session, compaction, transport);

        Assert.Equal(
            TelegramMessageProcessingStatus.Processed,
            await EnqueueAsync(fixture, session, 1, 10001));
        await compaction.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var third = EnqueueAsync(fixture, session, 3, 10001);
        var second = EnqueueAsync(fixture, session, 2, 10002);
        await WaitUntilAsync(() => transport.Deliveries.Count >= 2);

        Assert.All(
            transport.Deliveries.Take(2),
            delivery => Assert.Contains("отвечу чуть позже", delivery.Text));
        compaction.Release.TrySetResult();
        await Task.WhenAll(second, third);
        await WaitUntilAsync(() => transport.Deliveries.Count >= 4);

        Assert.Equal(
            [1L, 2L, 3L],
            Assert.IsType<RecordingTurnProcessor>(fixture.Processor).Order);
        Assert.Equal(2, transport.Deliveries.Count(delivery =>
            delivery.Text == "Готово, продолжаю с вашим сообщением."));
        Assert.Contains(transport.Deliveries, delivery => delivery.TelegramUserId == 10001);
        Assert.Contains(transport.Deliveries, delivery => delivery.TelegramUserId == 10002);
    }

    [Fact]
    public async Task CompactionWithoutWaitingMessages_SendsNoNotifications()
    {
        var session = CreateSession();
        var compaction = new BlockingCompactionService(session);
        var transport = new ConcurrentRecordingTransport();
        var fixture = CreateQueue(session, compaction, transport);

        var first = await EnqueueAsync(fixture, session, 1, 10001);
        compaction.Release.TrySetResult();
        await compaction.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50);

        Assert.Equal(TelegramMessageProcessingStatus.Processed, first);
        Assert.Empty(transport.Deliveries);
    }

    [Fact]
    public async Task FailedCompaction_NotifiesWaitingUserAndContinuesTurn()
    {
        var session = CreateSession();
        var compaction = new BlockingCompactionService(session) { Fail = true };
        var transport = new ConcurrentRecordingTransport();
        var fixture = CreateQueue(session, compaction, transport);

        await EnqueueAsync(fixture, session, 1, 10001);
        await compaction.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var waiting = EnqueueAsync(fixture, session, 2, 10002);
        await WaitUntilAsync(() => transport.Deliveries.Count >= 1);

        compaction.Release.TrySetResult();
        Assert.Equal(
            TelegramMessageProcessingStatus.Processed,
            await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
        await WaitUntilAsync(() => transport.Deliveries.Count >= 2);

        Assert.Contains(transport.Deliveries, delivery =>
            delivery.Text.Contains(
                "не удалось полностью",
                StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, compaction.ExecuteCount);
    }

    [Fact]
    public async Task FailedTurn_RemainsPersistedAndCanBeRecovered()
    {
        var session = CreateSession();
        var store = new InMemoryConversationStore([session]);
        var turn = CreateTurn(session, 6, 10001);
        Assert.True(await store.TryEnqueueAsync(turn));
        var processor = new FailOnceTurnProcessor();
        var queue = CreateQueue(
            session,
            new NoCompactionService(),
            new ConcurrentRecordingTransport(),
            store,
            processor).Queue;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            queue.ProcessEnqueuedAsync(turn));
        Assert.Single(await store.GetPendingAsync(session.Id));

        await queue.RecoverSessionAsync(session.Id);
        await WaitUntilAsync(async () =>
            (await store.GetPendingAsync(session.Id)).Count == 0);

        Assert.Equal(2, processor.AttemptCount);
    }

    [Fact]
    public async Task RecoverSession_ProcessesPersistedTurnWithoutOriginalHandler()
    {
        var session = CreateSession();
        var store = new InMemoryConversationStore([session]);
        var turn = CreateTurn(session, 7, 10001);
        Assert.True(await store.TryEnqueueAsync(turn));
        var processor = new RecordingTurnProcessor();
        var queue = CreateQueue(
            session,
            new NoCompactionService(),
            new ConcurrentRecordingTransport(),
            store,
            processor).Queue;

        await queue.RecoverSessionAsync(session.Id);
        await WaitUntilAsync(() => processor.Order.Count == 1);
        await WaitUntilAsync(async () =>
            (await store.GetPendingAsync(session.Id)).Count == 0);

        Assert.Equal([7L], processor.Order);
    }

    private static QueueFixture CreateQueue(
        Session session,
        IConversationCompactionService compactionService,
        ITelegramMessageTransport transport,
        InMemoryConversationStore? store = null,
        ITelegramQueuedTurnProcessor? processor = null)
    {
        store ??= new InMemoryConversationStore([session]);
        processor ??= new RecordingTurnProcessor();
        var queue = new TelegramSessionWorkQueue(
            new SessionTurnCoordinator(),
            compactionService,
            store,
            processor,
            transport,
            new TelegramAdapterOptions
            {
                SessionId = session.Id,
                ParticipantAUserId = 10001,
                ParticipantBUserId = 10002,
                ModelDisplayName = "Fake",
                DeliveryTimeout = TimeSpan.FromSeconds(2)
            },
            CompactionOptions(),
            NullLogger<TelegramSessionWorkQueue>.Instance);
        return new QueueFixture(queue, store, processor);
    }

    private static async Task<TelegramMessageProcessingStatus> EnqueueAsync(
        QueueFixture fixture,
        Session session,
        long updateId,
        long telegramUserId)
    {
        var turn = CreateTurn(session, updateId, telegramUserId);
        Assert.True(await fixture.Store.TryEnqueueAsync(turn));
        return await fixture.Queue.ProcessEnqueuedAsync(turn);
    }

    private static PendingExternalTurn CreateTurn(
        Session session,
        long updateId,
        long telegramUserId) => new(
        Guid.NewGuid(),
        session.Id,
        telegramUserId == 10001
            ? session.ParticipantA.Id
            : session.ParticipantB.Id,
        "telegram",
        updateId.ToString(CultureInfo.InvariantCulture),
        updateId,
        telegramUserId.ToString(CultureInfo.InvariantCulture),
        $"message-{updateId}",
        DateTimeOffset.UtcNow);

    private static ConversationCompactionOptions CompactionOptions() => new()
    {
        Enabled = true,
        TriggerMessageCount = 4,
        TriggerHistoryCharacters = 100,
        RetainRecentMessageCount = 2,
        RetainRecentCharacters = 50,
        MaxSummaryCharacters = 100,
        MaxOutputTokens = 100,
        OperationTimeout = TimeSpan.FromSeconds(5),
        RetryDelay = TimeSpan.FromMinutes(1)
    };

    private static Session CreateSession()
    {
        var participantA = new Participant(Guid.NewGuid(), "A");
        var participantB = new Participant(Guid.NewGuid(), "B");
        return new Session(Guid.NewGuid(), participantA, participantB);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= timeout)
            {
                throw new TimeoutException("The expected asynchronous condition was not met.");
            }

            await Task.Delay(10);
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var timeout = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!await condition())
        {
            if (DateTimeOffset.UtcNow >= timeout)
            {
                throw new TimeoutException("The expected asynchronous condition was not met.");
            }

            await Task.Delay(10);
        }
    }

    private sealed record QueueFixture(
        TelegramSessionWorkQueue Queue,
        InMemoryConversationStore Store,
        ITelegramQueuedTurnProcessor Processor);

    private sealed class RecordingTurnProcessor : ITelegramQueuedTurnProcessor
    {
        private readonly ConcurrentQueue<long> _order = [];

        public IReadOnlyList<long> Order => _order.ToArray();

        public Task<TelegramMessageProcessingStatus> ProcessAsync(
            PendingExternalTurn turn,
            CancellationToken cancellationToken = default)
        {
            _order.Enqueue(turn.SourceSequence);
            return Task.FromResult(TelegramMessageProcessingStatus.Processed);
        }
    }

    private sealed class FailOnceTurnProcessor : ITelegramQueuedTurnProcessor
    {
        public int AttemptCount { get; private set; }

        public Task<TelegramMessageProcessingStatus> ProcessAsync(
            PendingExternalTurn turn,
            CancellationToken cancellationToken = default)
        {
            AttemptCount++;
            if (AttemptCount == 1)
            {
                throw new InvalidOperationException("Simulated turn failure.");
            }

            return Task.FromResult(TelegramMessageProcessingStatus.Processed);
        }
    }

    private sealed class BlockingCompactionService(Session session)
        : IConversationCompactionService
    {
        private int _prepareCount;

        public TaskCompletionSource Entered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Completed { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Fail { get; init; }

        public int ExecuteCount { get; private set; }

        public Task<ConversationCompactionPlan?> PrepareAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _prepareCount);
            if (call != 2 || ExecuteCount > 0)
            {
                return Task.FromResult<ConversationCompactionPlan?>(null);
            }

            var message = new Message(
                Guid.NewGuid(),
                session.Id,
                session.ParticipantA.Id,
                null,
                MessageDirection.ParticipantToMediator,
                "old",
                DateTimeOffset.UtcNow);
            return Task.FromResult<ConversationCompactionPlan?>(new(
                session,
                new ConversationCompactionBatch(
                    session.Id,
                    0,
                    1,
                    null,
                    [new SequencedMessage(1, message)])));
        }

        public async Task<ConversationCompactionResult> ExecuteAsync(
            ConversationCompactionPlan plan,
            CancellationToken cancellationToken = default)
        {
            ExecuteCount++;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            Completed.TrySetResult();
            if (Fail)
            {
                throw new InvalidOperationException("Simulated compaction failure.");
            }

            return new ConversationCompactionResult(1, 1, 10);
        }
    }

    private sealed class NoCompactionService : IConversationCompactionService
    {
        public Task<ConversationCompactionPlan?> PrepareAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ConversationCompactionPlan?>(null);

        public Task<ConversationCompactionResult> ExecuteAsync(
            ConversationCompactionPlan plan,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class ConcurrentRecordingTransport : ITelegramMessageTransport
    {
        private readonly ConcurrentQueue<(long TelegramUserId, string Text)> _deliveries = [];

        public IReadOnlyList<(long TelegramUserId, string Text)> Deliveries =>
            _deliveries.ToArray();

        public Task SendTextMessageAsync(
            long telegramUserId,
            string text,
            CancellationToken cancellationToken = default)
        {
            _deliveries.Enqueue((telegramUserId, text));
            return Task.CompletedTask;
        }
    }
}
