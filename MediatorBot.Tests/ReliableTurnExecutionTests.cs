using MediatorBot.Core;
using MediatorBot.Infrastructure;
using MediatorBot.Telegram;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediatorBot.Tests;

public sealed class ReliableTurnExecutionTests
{
    private const long ParticipantAUserId = 10001;
    private const long ParticipantBUserId = 10002;

    [Fact]
    public async Task SqliteRestart_ReusesModelResultAndSkipsConfirmedRecipientDelivery()
    {
        using var database = new TemporaryDatabase();
        var options = CreateOptions();
        var firstStore = database.CreateStore();
        var firstRegistry = new TelegramParticipantRegistry(
            firstStore,
            firstStore,
            options);
        await firstRegistry.InitializeAsync();
        var session = await firstRegistry.GetSessionAsync();
        var turn = new PendingExternalTurn(
            Guid.NewGuid(),
            session.Id,
            session.ParticipantA.Id,
            "telegram",
            "501",
            501,
            ParticipantAUserId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "Первый turn",
            DateTimeOffset.UtcNow);
        Assert.True(await firstStore.TryEnqueueAsync(turn));
        var runtime = new CountingSendToBothRuntime();
        var transport = new FaultInjectingTransport
        {
            FailForTelegramUserId = ParticipantBUserId
        };
        var firstProcessor = CreateProcessor(
            firstStore,
            firstRegistry,
            runtime,
            transport,
            options);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            firstProcessor.ProcessAsync(turn));
        Assert.Equal(1, runtime.CallCount);
        Assert.Equal(
            [(ParticipantAUserId, "Для A")],
            transport.SuccessfulDeliveries);
        Assert.Equal(
            ["Первый turn", "Для A"],
            (await firstStore.GetHistoryAsync(session.Id)).Select(message => message.Text));

        var restartedStore = database.CreateStore();
        var restartedRegistry = new TelegramParticipantRegistry(
            restartedStore,
            restartedStore,
            options);
        await restartedRegistry.InitializeAsync();
        transport.FailForTelegramUserId = null;
        var rejectingRuntime = new RejectingModelRuntime();
        var restartedProcessor = CreateProcessor(
            restartedStore,
            restartedRegistry,
            rejectingRuntime,
            transport,
            options);
        var restoredTurn = Assert.Single(
            await restartedStore.GetPendingAsync(session.Id));

        Assert.Equal(
            TelegramMessageProcessingStatus.Processed,
            await restartedProcessor.ProcessAsync(restoredTurn));
        await restartedStore.CompleteAsync(session.Id, restoredTurn.Id);

        Assert.Equal(0, rejectingRuntime.CallCount);
        Assert.Equal(1, transport.SuccessfulDeliveries.Count(delivery =>
            delivery == (ParticipantAUserId, "Для A")));
        Assert.Equal(1, transport.SuccessfulDeliveries.Count(delivery =>
            delivery == (ParticipantBUserId, "Для B")));
        Assert.Equal(
            ["Первый turn", "Для A", "Для B"],
            (await restartedStore.GetHistoryAsync(session.Id))
                .Select(message => message.Text));
        Assert.Empty(await restartedStore.GetPendingAsync(session.Id));
    }

    [Fact]
    public async Task SqliteRestart_AfterIncomingCheckpointDoesNotDuplicateInput()
    {
        using var database = new TemporaryDatabase();
        var options = CreateOptions();
        var firstStore = database.CreateStore();
        var firstRegistry = new TelegramParticipantRegistry(
            firstStore,
            firstStore,
            options);
        await firstRegistry.InitializeAsync();
        var session = await firstRegistry.GetSessionAsync();
        var turn = new PendingExternalTurn(
            Guid.NewGuid(),
            session.Id,
            session.ParticipantA.Id,
            "telegram",
            "503",
            503,
            ParticipantAUserId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "Checkpoint входящего",
            DateTimeOffset.UtcNow);
        Assert.True(await firstStore.TryEnqueueAsync(turn));
        var failingRuntime = new RejectingModelRuntime();
        var transport = new FaultInjectingTransport();
        var firstProcessor = CreateProcessor(
            firstStore,
            firstRegistry,
            failingRuntime,
            transport,
            options);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            firstProcessor.ProcessAsync(turn));
        Assert.Equal(
            ["Checkpoint входящего"],
            (await firstStore.GetHistoryAsync(session.Id)).Select(message => message.Text));

        var restartedStore = database.CreateStore();
        var restartedRegistry = new TelegramParticipantRegistry(
            restartedStore,
            restartedStore,
            options);
        await restartedRegistry.InitializeAsync();
        var recoveryRuntime = new CountingNoActionRuntime();
        var restartedProcessor = CreateProcessor(
            restartedStore,
            restartedRegistry,
            recoveryRuntime,
            transport,
            options);

        Assert.Equal(
            TelegramMessageProcessingStatus.Processed,
            await restartedProcessor.ProcessAsync(Assert.Single(
                await restartedStore.GetPendingAsync(session.Id))));

        Assert.Equal(1, recoveryRuntime.CallCount);
        Assert.Equal(
            ["Checkpoint входящего"],
            (await restartedStore.GetHistoryAsync(session.Id))
                .Select(message => message.Text));
    }

    [Fact]
    public async Task SqliteRestart_AfterModelCheckpointDoesNotCallModelAgain()
    {
        using var database = new TemporaryDatabase();
        var options = CreateOptions();
        var firstStore = database.CreateStore();
        var firstRegistry = new TelegramParticipantRegistry(
            firstStore,
            firstStore,
            options);
        await firstRegistry.InitializeAsync();
        var session = await firstRegistry.GetSessionAsync();
        var turn = new PendingExternalTurn(
            Guid.NewGuid(),
            session.Id,
            session.ParticipantA.Id,
            "telegram",
            "502",
            502,
            ParticipantAUserId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "Checkpoint модели",
            DateTimeOffset.UtcNow);
        Assert.True(await firstStore.TryEnqueueAsync(turn));
        var runtime = new CountingSendToBothRuntime();
        var transport = new FaultInjectingTransport();
        var faultingStore = new FailBeforeDeliveryPlanStore(firstStore);
        var firstProcessor = CreateProcessor(
            firstStore,
            firstRegistry,
            runtime,
            transport,
            options,
            faultingStore);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            firstProcessor.ProcessAsync(turn));
        Assert.Equal(1, runtime.CallCount);
        Assert.Empty(transport.SuccessfulDeliveries);
        Assert.Equal(
            ["Checkpoint модели"],
            (await firstStore.GetHistoryAsync(session.Id)).Select(message => message.Text));

        var restartedStore = database.CreateStore();
        var restartedRegistry = new TelegramParticipantRegistry(
            restartedStore,
            restartedStore,
            options);
        await restartedRegistry.InitializeAsync();
        var rejectingRuntime = new RejectingModelRuntime();
        var restartedProcessor = CreateProcessor(
            restartedStore,
            restartedRegistry,
            rejectingRuntime,
            transport,
            options);

        Assert.Equal(
            TelegramMessageProcessingStatus.Processed,
            await restartedProcessor.ProcessAsync(Assert.Single(
                await restartedStore.GetPendingAsync(session.Id))));

        Assert.Equal(0, rejectingRuntime.CallCount);
        Assert.Equal(2, transport.SuccessfulDeliveries.Count);
    }

    private static TelegramQueuedTurnProcessor CreateProcessor(
        SqliteConversationStore store,
        TelegramParticipantRegistry registry,
        IModelRuntime runtime,
        ITelegramMessageTransport transport,
        TelegramAdapterOptions options,
        ITurnExecutionStore? turnExecutionStore = null)
    {
        var effectiveTurnExecutionStore = turnExecutionStore ?? store;
        var mediationService = new MediationService(
            store,
            runtime,
            new ConversationContextBuilder(store, store, 100));
        var dispatcher = new TelegramMediatorActionDispatcher(
            registry,
            transport,
            effectiveTurnExecutionStore,
            store,
            new TelegramTextChunker(),
            options,
            NullLogger<TelegramMediatorActionDispatcher>.Instance);
        return new TelegramQueuedTurnProcessor(
            registry,
            mediationService,
            dispatcher,
            effectiveTurnExecutionStore,
            new MediatorActionSerializer(),
            options,
            NullLogger<TelegramQueuedTurnProcessor>.Instance);
    }

    private static TelegramAdapterOptions CreateOptions() => new()
    {
        SessionId = Guid.NewGuid(),
        ParticipantAUserId = ParticipantAUserId,
        ParticipantBUserId = ParticipantBUserId,
        ParticipantADisplayName = "A",
        ParticipantBDisplayName = "B",
        ModelDisplayName = "test-model",
        DeliveryTimeout = TimeSpan.FromSeconds(10),
        DeliveryRecordingTimeout = TimeSpan.FromSeconds(10)
    };

    private sealed class CountingSendToBothRuntime : IModelRuntime
    {
        public int CallCount { get; private set; }

        public Task<ModelResult> ProcessAsync(
            ConversationContext context,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new ModelResult([
                new SendToBoth(
                    "Для A",
                    "Для B",
                    DisclosureDecision.MediatorDisclosure)
            ]));
        }
    }

    private sealed class CountingNoActionRuntime : IModelRuntime
    {
        public int CallCount { get; private set; }

        public Task<ModelResult> ProcessAsync(
            ConversationContext context,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new ModelResult([new NoAction()]));
        }
    }

    private sealed class RejectingModelRuntime : IModelRuntime
    {
        public int CallCount { get; private set; }

        public Task<ModelResult> ProcessAsync(
            ConversationContext context,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            throw new InvalidOperationException(
                "The model must not be called during recovery.");
        }
    }

    private sealed class FailBeforeDeliveryPlanStore(ITurnExecutionStore inner)
        : ITurnExecutionStore
    {
        public Task<string?> GetModelResultAsync(
            Guid sessionId,
            Guid turnId,
            CancellationToken cancellationToken = default) =>
            inner.GetModelResultAsync(sessionId, turnId, cancellationToken);

        public Task SaveModelResultAsync(
            Guid sessionId,
            Guid turnId,
            string modelResultJson,
            CancellationToken cancellationToken = default) =>
            inner.SaveModelResultAsync(
                sessionId,
                turnId,
                modelResultJson,
                cancellationToken);

        public Task<IReadOnlyList<TurnDelivery>> EnsureDeliveryPlanAsync(
            TurnDeliveryPlan plan,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "Simulated crash before creating the delivery plan.");

        public Task MarkDeliveryAttemptingAsync(
            Guid sessionId,
            Guid turnId,
            Guid deliveryId,
            DateTimeOffset attemptedAt,
            CancellationToken cancellationToken = default) =>
            inner.MarkDeliveryAttemptingAsync(
                sessionId,
                turnId,
                deliveryId,
                attemptedAt,
                cancellationToken);

        public Task RecordDeliveryAsync(
            Guid sessionId,
            Guid turnId,
            Guid deliveryId,
            DateTimeOffset deliveredAt,
            CancellationToken cancellationToken = default) =>
            inner.RecordDeliveryAsync(
                sessionId,
                turnId,
                deliveryId,
                deliveredAt,
                cancellationToken);
    }

    private sealed class FaultInjectingTransport : ITelegramMessageTransport
    {
        public List<(long TelegramUserId, string Text)> SuccessfulDeliveries { get; } = [];

        public long? FailForTelegramUserId { get; set; }

        public Task SendTextMessageAsync(
            long telegramUserId,
            string text,
            CancellationToken cancellationToken = default)
        {
            if (FailForTelegramUserId == telegramUserId)
            {
                throw new InvalidOperationException("Simulated Telegram failure.");
            }

            SuccessfulDeliveries.Add((telegramUserId, text));
            return Task.CompletedTask;
        }
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "MediatorBot.Tests",
            Guid.NewGuid().ToString("N"));

        public TemporaryDatabase()
        {
            Directory.CreateDirectory(_directory);
            Path = System.IO.Path.Combine(_directory, "mediator-bot.db");
        }

        public string Path { get; }

        public SqliteConversationStore CreateStore() => new(
            new SqliteConversationStoreOptions(Path, "reliable-turn-test-key"));

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
