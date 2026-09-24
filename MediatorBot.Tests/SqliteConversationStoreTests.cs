using System.Text;
using MediatorBot.Core;
using MediatorBot.Infrastructure;
using Microsoft.Data.Sqlite;

namespace MediatorBot.Tests;

public sealed class SqliteConversationStoreTests
{
    [Fact]
    public async Task Store_CreatesSessionAndRestoresParticipantsAndOrderedHistory()
    {
        using var database = new TemporaryDatabase();
        var firstStore = database.CreateStore();
        var (session, participantA, participantB) = CreateSession();
        await firstStore.CreateSessionAsync(session);

        var start = DateTimeOffset.UtcNow;
        var messages = new[]
        {
            Incoming(session, participantA, "A -> mediator", start),
            Outgoing(session, participantA, "mediator -> A", start.AddMilliseconds(1)),
            Incoming(session, participantB, "B -> mediator", start.AddMilliseconds(2)),
            Outgoing(session, participantB, "mediator -> B", start.AddMilliseconds(3))
        };

        foreach (var message in messages)
        {
            await firstStore.SaveMessageAsync(message);
        }

        var restartedStore = database.CreateStore();
        var restoredSession = await restartedStore.GetSessionAsync(session.Id);
        var restoredHistory = await restartedStore.GetHistoryAsync(session.Id);

        Assert.NotNull(restoredSession);
        Assert.Equal(session.Id, restoredSession.Id);
        Assert.Equal(session.CreatedAt, restoredSession.CreatedAt);
        Assert.Equal(participantA, restoredSession.ParticipantA);
        Assert.Equal(participantB, restoredSession.ParticipantB);
        Assert.Equal(messages.Select(message => message.Id), restoredHistory.Select(message => message.Id));
        Assert.Equal(
            [
                MessageDirection.ParticipantToMediator,
                MessageDirection.MediatorToParticipant,
                MessageDirection.ParticipantToMediator,
                MessageDirection.MediatorToParticipant
            ],
            restoredHistory.Select(message => message.Direction));
    }

    [Fact]
    public async Task Store_DoesNotMixHistoriesOfDifferentSessions()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore();
        var (firstSession, firstParticipant, _) = CreateSession();
        var (secondSession, secondParticipant, _) = CreateSession();
        await store.CreateSessionAsync(firstSession);
        await store.CreateSessionAsync(secondSession);

        await store.SaveMessageAsync(Incoming(
            firstSession,
            firstParticipant,
            "Первая",
            DateTimeOffset.UtcNow));
        await store.SaveMessageAsync(Incoming(
            secondSession,
            secondParticipant,
            "Вторая",
            DateTimeOffset.UtcNow));

        var firstHistory = await store.GetHistoryAsync(firstSession.Id);
        var secondHistory = await store.GetHistoryAsync(secondSession.Id);

        Assert.Equal("Первая", Assert.Single(firstHistory).Text);
        Assert.Equal("Вторая", Assert.Single(secondHistory).Text);
    }

    [Fact]
    public async Task RestartedStore_GivesPreviousIncomingAndOutgoingHistoryToModel()
    {
        using var database = new TemporaryDatabase();
        var (session, participantA, participantB) = CreateSession();
        var firstStore = database.CreateStore();
        await firstStore.CreateSessionAsync(session);
        var firstService = new MediationService(
            firstStore,
            new FakeModelRuntime(),
            new ConversationContextBuilder(firstStore, firstStore, 100));

        var firstActions = await firstService.HandleMessageAsync(
            session.Id,
            participantA.Id,
            "До перезапуска");
        var firstReply = Assert.IsType<SendToParticipant>(Assert.Single(firstActions));
        await new MediatorDeliveryRecorder(firstStore).RecordDeliveredAsync(
            session.Id,
            firstReply.ParticipantId,
            firstReply.Text);

        var restartedStore = database.CreateStore();
        var runtime = new CapturingModelRuntime();
        var restartedService = new MediationService(
            restartedStore,
            runtime,
            new ConversationContextBuilder(restartedStore, restartedStore, 100));
        await restartedService.HandleMessageAsync(
            session.Id,
            participantB.Id,
            "После перезапуска");

        var context = Assert.Single(runtime.Contexts);
        Assert.Collection(
            context.History,
            message => Assert.Equal("До перезапуска", message.Text),
            message =>
            {
                Assert.Equal(MessageDirection.MediatorToParticipant, message.Direction);
                Assert.Equal(participantA.Id, message.RecipientId);
            },
            message => Assert.Equal("После перезапуска", message.Text));
    }

    [Fact]
    public async Task ParticipantDisplayNames_CanBeUpdatedWithoutChangingIdentityOrHistory()
    {
        using var database = new TemporaryDatabase();
        var (session, participantA, participantB) = CreateSession();
        var store = database.CreateStore();
        await store.CreateSessionAsync(session);
        var message = Incoming(
            session,
            participantA,
            "Сообщение до переименования",
            DateTimeOffset.UtcNow);
        await store.SaveMessageAsync(message);

        await store.UpdateParticipantDisplayNamesAsync(
            session.Id,
            new Dictionary<Guid, string>
            {
                [participantA.Id] = "Анна",
                [participantB.Id] = "Борис"
            });

        var restartedStore = database.CreateStore();
        var updatedSession = await restartedStore.GetSessionAsync(session.Id);
        var history = await restartedStore.GetHistoryAsync(session.Id);

        Assert.NotNull(updatedSession);
        Assert.Equal(participantA.Id, updatedSession.ParticipantA.Id);
        Assert.Equal("Анна", updatedSession.ParticipantA.DisplayName);
        Assert.Equal(participantB.Id, updatedSession.ParticipantB.Id);
        Assert.Equal("Борис", updatedSession.ParticipantB.DisplayName);
        Assert.Equal(message.Id, Assert.Single(history).Id);
    }

    [Fact]
    public async Task MediatedRequestLifecycle_PersistsAcrossStoreRestart()
    {
        using var database = new TemporaryDatabase();
        var (session, participantA, participantB) = CreateSession();
        var store = database.CreateStore();
        await store.CreateSessionAsync(session);
        var request = new MediatedRequest(
            Guid.NewGuid(),
            session.Id,
            participantA.Id,
            participantB.Id,
            "Уточнить, готов ли B ответить",
            MediatedRequestStatus.PendingDelivery,
            DateTimeOffset.UtcNow);

        await store.CreateAsync(request);
        Assert.Empty(await store.GetOpenAsync(session.Id));
        await store.MarkAwaitingResponseAsync(session.Id, request.Id);
        await store.CreateAsync(request);
        await store.MarkAwaitingResponseAsync(session.Id, request.Id);

        var restartedStore = database.CreateStore();
        var restored = Assert.Single(await restartedStore.GetOpenAsync(session.Id));
        Assert.Equal(request.Id, restored.Id);
        Assert.Equal(request.Summary, restored.Summary);
        Assert.Equal(MediatedRequestStatus.AwaitingResponse, restored.Status);

        await restartedStore.ResolveAsync(
            session.Id,
            request.Id,
            MediatedRequestStatus.Declined,
            DateTimeOffset.UtcNow);
        await restartedStore.ResolveAsync(
            session.Id,
            request.Id,
            MediatedRequestStatus.Declined,
            DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.Empty(await database.CreateStore().GetOpenAsync(session.Id));
    }

    [Fact]
    public async Task ExternalUpdateRegistration_PersistsAcrossStoreRestart()
    {
        using var database = new TemporaryDatabase();
        var (session, _, _) = CreateSession();
        var firstStore = database.CreateStore();
        await firstStore.CreateSessionAsync(session);

        var firstRegistration = await firstStore.TryRegisterAsync(
            "test",
            session.Id,
            "update-1");
        var restartedStore = database.CreateStore();
        var duplicateRegistration = await restartedStore.TryRegisterAsync(
            "test",
            session.Id,
            "update-1");

        Assert.True(firstRegistration);
        Assert.False(duplicateRegistration);
    }

    [Fact]
    public async Task PendingTurn_RegistrationIsAtomicAndSurvivesRestartUntilCompleted()
    {
        using var database = new TemporaryDatabase();
        var (session, participantA, _) = CreateSession();
        var firstStore = database.CreateStore();
        await firstStore.CreateSessionAsync(session);
        var turn = new PendingExternalTurn(
            Guid.NewGuid(),
            session.Id,
            participantA.Id,
            "telegram",
            "42",
            42,
            "10001",
            "Сохранённый turn",
            DateTimeOffset.UtcNow);

        Assert.True(await firstStore.TryEnqueueAsync(turn));
        Assert.False(await firstStore.TryEnqueueAsync(turn with { Id = Guid.NewGuid() }));

        var restartedStore = database.CreateStore();
        var restored = Assert.Single(await restartedStore.GetPendingAsync(session.Id));
        Assert.Equal(turn, restored);

        await restartedStore.CompleteAsync(session.Id, turn.Id);
        Assert.Empty(await database.CreateStore().GetPendingAsync(session.Id));
        Assert.False(await database.CreateStore().TryRegisterAsync(
            "telegram",
            session.Id,
            "42"));
    }

    [Fact]
    public async Task PendingTurnIncomingMessage_IsRecordedIdempotently()
    {
        using var database = new TemporaryDatabase();
        var (session, participantA, _) = CreateSession();
        var store = database.CreateStore();
        await store.CreateSessionAsync(session);
        var turn = new PendingExternalTurn(
            Guid.NewGuid(),
            session.Id,
            participantA.Id,
            "telegram",
            "incoming-1",
            1,
            "10001",
            "Один раз",
            DateTimeOffset.UtcNow);
        Assert.True(await store.TryEnqueueAsync(turn));
        var incoming = new Message(
            turn.Id,
            session.Id,
            participantA.Id,
            null,
            MessageDirection.ParticipantToMediator,
            turn.Text,
            turn.CreatedAt);

        await store.SaveMessageAsync(incoming);
        await database.CreateStore().SaveMessageAsync(incoming);

        Assert.Equal(incoming, Assert.Single(
            await database.CreateStore().GetHistoryAsync(session.Id)));
        var conflicting = new Message(
            incoming.Id,
            incoming.SessionId,
            incoming.AuthorId,
            incoming.RecipientId,
            incoming.Direction,
            "Другое",
            incoming.CreatedAt);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            database.CreateStore().SaveMessageAsync(conflicting));
    }

    [Fact]
    public async Task ParticipantIdentityBindings_PersistAndRejectChangedMapping()
    {
        using var database = new TemporaryDatabase();
        var (session, participantA, participantB) = CreateSession();
        var firstStore = database.CreateStore();
        await firstStore.CreateSessionAsync(session);
        ParticipantIdentityBinding[] bindings =
        [
            new(participantA.Id, "external-a"),
            new(participantB.Id, "external-b")
        ];
        await firstStore.EnsureBindingsAsync(session.Id, "test", bindings);

        var restartedStore = database.CreateStore();
        await restartedStore.EnsureBindingsAsync(session.Id, "test", bindings);

        ParticipantIdentityBinding[] changedBindings =
        [
            new(participantA.Id, "external-b"),
            new(participantB.Id, "external-a")
        ];
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            restartedStore.EnsureBindingsAsync(
                session.Id,
                "test",
                changedBindings));
    }

    [Fact]
    public async Task CompactionSnapshotAndDeletion_PersistAcrossStoreRestart()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore();
        var (session, participantA, _) = CreateSession();
        await store.CreateSessionAsync(session);
        var messages = Enumerable.Range(1, 4)
            .Select(index => Incoming(
                session,
                participantA,
                $"message-{index}",
                DateTimeOffset.UtcNow.AddSeconds(index)))
            .ToArray();
        foreach (var message in messages)
        {
            await store.SaveMessageAsync(message);
        }

        var batch = await store.GetCompactionBatchAsync(
            session.Id,
            triggerMessageCount: 4,
            triggerCharacterCount: 10_000,
            retainRecentMessageCount: 2,
            retainRecentCharacterCount: 5_000);
        Assert.NotNull(batch);
        var content = new ConversationSummaryContent(
            "private-a",
            "private-b",
            "shared",
            "safety");
        await store.CommitCompactionAsync(
            session.Id,
            batch.ExpectedSummaryVersion,
            batch.CompactedThroughSequence,
            content,
            DateTimeOffset.UtcNow);

        var restartedStore = database.CreateStore();
        var restoredSummary = await restartedStore.GetSummaryAsync(session.Id);
        var restoredHistory = await restartedStore.GetHistoryAsync(session.Id);

        Assert.NotNull(restoredSummary);
        Assert.Equal(1, restoredSummary.Version);
        Assert.Equal(content, restoredSummary.Content);
        Assert.Equal(
            messages.Skip(2).Select(message => message.Id),
            restoredHistory.Select(message => message.Id));
    }

    [Fact]
    public async Task CompactionVersionConflict_DoesNotDeleteMessages()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore();
        var (session, participantA, _) = CreateSession();
        await store.CreateSessionAsync(session);
        foreach (var index in Enumerable.Range(1, 4))
        {
            await store.SaveMessageAsync(Incoming(
                session,
                participantA,
                $"message-{index}",
                DateTimeOffset.UtcNow.AddSeconds(index)));
        }

        var batch = await store.GetCompactionBatchAsync(
            session.Id,
            4,
            10_000,
            2,
            5_000);
        Assert.NotNull(batch);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CommitCompactionAsync(
                session.Id,
                expectedSummaryVersion: 1,
                batch.CompactedThroughSequence,
                new ConversationSummaryContent("", "", "", ""),
                DateTimeOffset.UtcNow));

        Assert.Equal(4, (await store.GetHistoryAsync(session.Id)).Count);
        Assert.Null(await store.GetSummaryAsync(session.Id));
    }

    [Fact]
    public async Task VersionSixDatabase_IsMigratedSequentiallyWithoutLosingPendingTurn()
    {
        using var database = new TemporaryDatabase();
        var (session, participantA, _) = CreateSession();
        var originalStore = database.CreateStore();
        await originalStore.CreateSessionAsync(session);
        await originalStore.SaveMessageAsync(Incoming(
            session,
            participantA,
            "old-1",
            DateTimeOffset.UtcNow.AddMinutes(-2)));
        await originalStore.SaveMessageAsync(Incoming(
            session,
            participantA,
            "old-2",
            DateTimeOffset.UtcNow.AddMinutes(-1)));
        var turn = new PendingExternalTurn(
            Guid.NewGuid(),
            session.Id,
            participantA.Id,
            "telegram",
            "migration-42",
            42,
            "10001",
            "Сохранённый до миграции turn",
            DateTimeOffset.UtcNow);
        Assert.True(await originalStore.TryEnqueueAsync(turn));
        var legacyMessage = new Message(
            Guid.NewGuid(),
            session.Id,
            participantA.Id,
            null,
            MessageDirection.ParticipantToMediator,
            turn.Text,
            turn.CreatedAt.AddMilliseconds(1));
        await originalStore.SaveMessageAsync(legacyMessage);

        await ExecuteDirectAsync(
            database.Path,
            """
            DROP TABLE TurnDeliveries;
            DROP INDEX IX_PendingTurns_SessionId_Status_Order;
            ALTER TABLE PendingTurns DROP COLUMN FailureType;
            ALTER TABLE PendingTurns DROP COLUMN FailedAt;
            ALTER TABLE PendingTurns DROP COLUMN LastAttemptAt;
            ALTER TABLE PendingTurns DROP COLUMN AttemptCount;
            ALTER TABLE PendingTurns DROP COLUMN Status;
            ALTER TABLE PendingTurns DROP COLUMN ModelResultJson;
            ALTER TABLE PendingTurns DROP COLUMN IncomingRecordedAt;
            PRAGMA user_version = 6;
            """);

        var migratedStore = database.CreateStore();
        await migratedStore.InitializeAsync();
        Assert.Equal(turn, Assert.Single(await migratedStore.GetPendingAsync(session.Id)));
        var compactionBatch = await migratedStore.GetCompactionBatchAsync(
            session.Id,
            triggerMessageCount: 2,
            triggerCharacterCount: 10_000,
            retainRecentMessageCount: 1,
            retainRecentCharacterCount: 5_000);
        Assert.NotNull(compactionBatch);
        Assert.DoesNotContain(
            compactionBatch.Messages,
            message => message.Message.Id == legacyMessage.Id);
        await migratedStore.CommitCompactionAsync(
            session.Id,
            compactionBatch.ExpectedSummaryVersion,
            compactionBatch.CompactedThroughSequence,
            new ConversationSummaryContent("", "", "", ""),
            DateTimeOffset.UtcNow);

        await migratedStore.SaveMessageAsync(new Message(
            turn.Id,
            session.Id,
            participantA.Id,
            null,
            MessageDirection.ParticipantToMediator,
            turn.Text,
            turn.CreatedAt));
        var migratedHistory = await migratedStore.GetHistoryAsync(session.Id);
        var migratedIncoming = Assert.Single(
            migratedHistory,
            message => message.Text == turn.Text);
        Assert.Equal(turn.Id, migratedIncoming.Id);

        Assert.Equal(1, await migratedStore.BeginAttemptAsync(
            session.Id,
            turn.Id,
            DateTimeOffset.UtcNow));
        await migratedStore.MarkFailedAsync(
            session.Id,
            turn.Id,
            "TestFailure",
            DateTimeOffset.UtcNow);
        Assert.Empty(await migratedStore.GetPendingAsync(session.Id));
        Assert.Equal(1, await migratedStore.GetFailedCountAsync(
            session.Id,
            participantA.Id));
        Assert.Equal(1, await migratedStore.RetryFailedAsync(
            session.Id,
            participantA.Id));
        var retried = Assert.Single(await migratedStore.GetPendingAsync(session.Id));
        Assert.Equal(0, retried.AttemptCount);

        await migratedStore.SaveModelResultAsync(
            session.Id,
            turn.Id,
            new MediatorActionSerializer().Serialize([new NoAction()]));
        Assert.NotNull(await migratedStore.GetModelResultAsync(session.Id, turn.Id));
    }

    [Fact]
    public async Task VersionSevenDatabase_IsMigratedWithoutLosingExecutionCheckpoints()
    {
        using var database = new TemporaryDatabase();
        var (session, participantA, _) = CreateSession();
        var originalStore = database.CreateStore();
        await originalStore.CreateSessionAsync(session);
        var turn = new PendingExternalTurn(
            Guid.NewGuid(),
            session.Id,
            participantA.Id,
            "telegram",
            "migration-v7",
            43,
            "10001",
            "Pending v7 turn",
            DateTimeOffset.UtcNow);
        Assert.True(await originalStore.TryEnqueueAsync(turn));
        var modelResult = new MediatorActionSerializer().Serialize([new NoAction()]);
        await originalStore.SaveModelResultAsync(session.Id, turn.Id, modelResult);
        var plan = new TurnDeliveryPlan(
            turn.Id,
            session.Id,
            participantA.Id,
            "migration-v7-delivery",
            ["saved delivery"],
            nameof(SendToParticipant),
            DisclosureDecision.PrivateResponse,
            turn.CreatedAt);
        var delivery = Assert.Single(await originalStore.EnsureDeliveryPlanAsync(plan));
        await originalStore.MarkDeliveryAttemptingAsync(
            session.Id,
            turn.Id,
            delivery.Id,
            DateTimeOffset.UtcNow);

        await ExecuteDirectAsync(
            database.Path,
            """
            DROP INDEX IX_PendingTurns_SessionId_Status_Order;
            ALTER TABLE PendingTurns DROP COLUMN FailureType;
            ALTER TABLE PendingTurns DROP COLUMN FailedAt;
            ALTER TABLE PendingTurns DROP COLUMN LastAttemptAt;
            ALTER TABLE PendingTurns DROP COLUMN AttemptCount;
            ALTER TABLE PendingTurns DROP COLUMN Status;
            PRAGMA user_version = 7;
            """);

        var migratedStore = database.CreateStore();
        await migratedStore.InitializeAsync();

        Assert.Equal(turn, Assert.Single(await migratedStore.GetPendingAsync(session.Id)));
        Assert.Equal(modelResult, await migratedStore.GetModelResultAsync(
            session.Id,
            turn.Id));
        Assert.Equal(
            TurnDeliveryStatus.Attempting,
            Assert.Single(await migratedStore.EnsureDeliveryPlanAsync(plan)).Status);
        Assert.Equal(1, await migratedStore.BeginAttemptAsync(
            session.Id,
            turn.Id,
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task InitiativeDecisionAndDelivery_SurviveRestartAndRecordHistory()
    {
        using var database = new TemporaryDatabase();
        var (session, participantA, _) = CreateSession();
        var store = database.CreateStore();
        await store.CreateSessionAsync(session);
        var incoming = Incoming(
            session,
            participantA,
            "Контекст инициативы",
            DateTimeOffset.UtcNow.AddHours(-2));
        await store.SaveMessageAsync(incoming);
        var decision = new InitiativeDecision(
            Guid.NewGuid(),
            session.Id,
            incoming.Id,
            DateTimeOffset.UtcNow,
            RelationshipPhase.CoolingDown,
            InitiativeConfidence.Medium,
            InitiativeDecisionKind.ContactParticipant,
            participantA.Id,
            InitiativeIntent.CheckIn,
            InitiativeReasonCode.RecentConflict,
            "Уместна короткая проверка состояния.",
            "abcdef",
            null,
            DateTimeOffset.UtcNow.AddHours(2),
            InitiativeDecisionStatus.PendingDelivery);
        Assert.True(await store.TrySaveDecisionAsync(decision, null));
        var plan = new InitiativeDeliveryPlan(
            decision.Id,
            session.Id,
            participantA.Id,
            "participant-a",
            ["ab", "cd", "ef"],
            decision.EvaluatedAt);
        var deliveries = await store.EnsureInitiativeDeliveryPlanAsync(plan);
        await store.MarkInitiativeDeliveryAttemptingAsync(
            session.Id,
            decision.Id,
            deliveries[0].Id,
            DateTimeOffset.UtcNow);
        await store.RecordInitiativeDeliveryAsync(
            session.Id,
            decision.Id,
            deliveries[0].Id,
            DateTimeOffset.UtcNow);

        var restarted = database.CreateStore();
        var restoredDecision = Assert.Single(
            await restarted.GetPendingDeliveryDecisionsAsync(session.Id));
        var restoredDeliveries = await restarted.EnsureInitiativeDeliveryPlanAsync(plan);
        Assert.Equal(decision.Id, restoredDecision.Id);
        Assert.Equal(InitiativeDeliveryStatus.Delivered, restoredDeliveries[0].Status);
        foreach (var delivery in restoredDeliveries.Skip(1))
        {
            await restarted.MarkInitiativeDeliveryAttemptingAsync(
                session.Id,
                decision.Id,
                delivery.Id,
                DateTimeOffset.UtcNow);
            await restarted.RecordInitiativeDeliveryAsync(
                session.Id,
                decision.Id,
                delivery.Id,
                DateTimeOffset.UtcNow);
        }

        await restarted.MarkDecisionStatusAsync(
            session.Id,
            decision.Id,
            InitiativeDecisionStatus.Delivered,
            DateTimeOffset.UtcNow);
        var outgoing = (await restarted.GetHistoryAsync(session.Id))
            .Single(message => message.Direction == MessageDirection.MediatorToParticipant);
        Assert.Equal("abcdef", outgoing.Text);
        Assert.Equal(1, await restarted.CountDeliveredContactsAsync(
            session.Id,
            participantA.Id,
            DateTimeOffset.UtcNow.AddDays(-1)));
    }

    [Fact]
    public async Task InitiativePreferences_PersistOptOutAndTemporaryPause()
    {
        using var database = new TemporaryDatabase();
        var (session, participantA, participantB) = CreateSession();
        var store = database.CreateStore();
        await store.CreateSessionAsync(session);
        var now = DateTimeOffset.UtcNow;
        var incoming = Incoming(
            session,
            participantB,
            "Дайте мне пространство",
            now.AddMinutes(-30));
        await store.SaveMessageAsync(incoming);
        var pauseDecision = new InitiativeDecision(
            Guid.NewGuid(),
            session.Id,
            incoming.Id,
            now,
            RelationshipPhase.CoolingDown,
            InitiativeConfidence.High,
            InitiativeDecisionKind.ReevaluateLater,
            null,
            InitiativeIntent.Observe,
            InitiativeReasonCode.RequestedSpace,
            "Участник явно попросил временно не инициировать контакт.",
            null,
            null,
            now.AddHours(2),
            InitiativeDecisionStatus.NoDelivery,
            PauseParticipantId: participantB.Id,
            PauseUntil: now.AddHours(4));
        Assert.True(await store.TrySaveDecisionAsync(pauseDecision, null));

        await store.SetParticipantEnabledAsync(
            session.Id,
            participantA.Id,
            false,
            now);
        var restarted = database.CreateStore();
        var preferences = await restarted.GetParticipantPreferencesAsync(session);
        var restoredDecision = await restarted.GetLatestDecisionAsync(session.Id);
        Assert.False(preferences.Single(item =>
            item.ParticipantId == participantA.Id).IsEnabled);
        Assert.Equal(
            now.AddHours(4),
            preferences.Single(item => item.ParticipantId == participantB.Id).PauseUntil);
        Assert.Equal(participantB.Id, restoredDecision?.PauseParticipantId);
        Assert.Equal(now.AddHours(4), restoredDecision?.PauseUntil);
    }

    [Fact]
    public async Task VersionEightDatabase_IsMigratedWithInitiativeStorage()
    {
        using var database = new TemporaryDatabase();
        var (session, _, _) = CreateSession();
        var store = database.CreateStore();
        await store.CreateSessionAsync(session);
        await ExecuteDirectAsync(
            database.Path,
            """
            DROP TABLE InitiativeDeliveries;
            DROP TABLE InitiativeDecisions;
            DROP TABLE InitiativeParticipantPreferences;
            PRAGMA user_version = 8;
            """);

        var migrated = database.CreateStore();
        await migrated.InitializeAsync();

        var preferences = await migrated.GetParticipantPreferencesAsync(session);
        Assert.Equal(2, preferences.Count);
        Assert.All(preferences, preference => Assert.True(preference.IsEnabled));
    }

    [Fact]
    public async Task DatabaseWithNewerSchemaVersion_IsRejected()
    {
        using var database = new TemporaryDatabase();
        await database.CreateStore().InitializeAsync();
        await ExecuteDirectAsync(database.Path, "PRAGMA user_version = 999;");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            database.CreateStore().InitializeAsync());
        Assert.Contains("newer than supported version", exception.Message);
    }

    [Fact]
    public async Task DeliveryPlanAndModelResult_SurviveRestartAndFormOneLogicalMessage()
    {
        using var database = new TemporaryDatabase();
        var (session, participantA, _) = CreateSession();
        var store = database.CreateStore();
        await store.CreateSessionAsync(session);
        var turn = new PendingExternalTurn(
            Guid.NewGuid(),
            session.Id,
            participantA.Id,
            "telegram",
            "delivery-1",
            1,
            "10001",
            "input",
            DateTimeOffset.UtcNow);
        Assert.True(await store.TryEnqueueAsync(turn));
        var serializedResult = new MediatorActionSerializer().Serialize([
            new SendToParticipant(
                participantA.Id,
                "abcdef",
                DisclosureDecision.PrivateResponse)
        ]);
        await store.SaveModelResultAsync(session.Id, turn.Id, serializedResult);
        var plan = new TurnDeliveryPlan(
            turn.Id,
            session.Id,
            participantA.Id,
            "0:participant",
            ["ab", "cd", "ef"],
            nameof(SendToParticipant),
            DisclosureDecision.PrivateResponse,
            turn.CreatedAt);
        var deliveries = await store.EnsureDeliveryPlanAsync(plan);
        await store.MarkDeliveryAttemptingAsync(
            session.Id,
            turn.Id,
            deliveries[0].Id,
            DateTimeOffset.UtcNow);
        await store.RecordDeliveryAsync(
            session.Id,
            turn.Id,
            deliveries[0].Id,
            DateTimeOffset.UtcNow);

        var restartedStore = database.CreateStore();
        Assert.Equal(
            serializedResult,
            await restartedStore.GetModelResultAsync(session.Id, turn.Id));
        var rechunkedPlan = plan with { Chunks = ["abc", "def"] };
        var restored = await restartedStore.EnsureDeliveryPlanAsync(rechunkedPlan);
        Assert.Equal(3, restored.Count);
        Assert.Equal(TurnDeliveryStatus.Delivered, restored[0].Status);
        Assert.All(restored.Skip(1), delivery =>
            Assert.Equal(TurnDeliveryStatus.Pending, delivery.Status));
        Assert.Equal(
            "ab",
            Assert.Single(await restartedStore.GetHistoryAsync(session.Id)).Text);

        foreach (var delivery in restored.Skip(1))
        {
            await restartedStore.MarkDeliveryAttemptingAsync(
                session.Id,
                turn.Id,
                delivery.Id,
                DateTimeOffset.UtcNow);
            await restartedStore.RecordDeliveryAsync(
                session.Id,
                turn.Id,
                delivery.Id,
                DateTimeOffset.UtcNow);
        }

        var outgoing = Assert.Single(await restartedStore.GetHistoryAsync(session.Id));
        Assert.Equal("abcdef", outgoing.Text);
        Assert.Equal(deliveries[0].LogicalMessageId, outgoing.Id);
    }

    [Fact]
    public async Task DatabaseFile_IsEncryptedAndCannotBeOpenedWithoutKey()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore();
        var (session, participantA, _) = CreateSession();
        await store.CreateSessionAsync(session);
        await store.SaveMessageAsync(Incoming(
            session,
            participantA,
            "Очень секретный текст",
            DateTimeOffset.UtcNow));

        SqliteConnection.ClearAllPools();
        var bytes = await File.ReadAllBytesAsync(database.Path);
        var header = Encoding.ASCII.GetString(bytes, 0, Math.Min(16, bytes.Length));
        Assert.NotEqual("SQLite format 3\0", header);
        Assert.DoesNotContain("Очень секретный текст", Encoding.UTF8.GetString(bytes));

        var storeWithWrongKey = new SqliteConversationStore(
            new SqliteConversationStoreOptions(database.Path, "wrong-key"));
        await Assert.ThrowsAsync<SqliteException>(() => storeWithWrongKey.InitializeAsync());
    }

    private static async Task ExecuteDirectAsync(string path, string sql)
    {
        SqliteConnection.ClearAllPools();
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using (var keyCommand = connection.CreateCommand())
        {
            keyCommand.CommandText = "PRAGMA key = 'integration-test-key';";
            await keyCommand.ExecuteNonQueryAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static (Session Session, Participant ParticipantA, Participant ParticipantB) CreateSession()
    {
        var participantA = new Participant(Guid.NewGuid(), "A");
        var participantB = new Participant(Guid.NewGuid(), "B");
        return (
            new Session(Guid.NewGuid(), participantA, participantB),
            participantA,
            participantB);
    }

    private static Message Incoming(
        Session session,
        Participant author,
        string text,
        DateTimeOffset createdAt) => new(
            Guid.NewGuid(),
            session.Id,
            author.Id,
            null,
            MessageDirection.ParticipantToMediator,
            text,
            createdAt);

    private static Message Outgoing(
        Session session,
        Participant recipient,
        string text,
        DateTimeOffset createdAt) => new(
            Guid.NewGuid(),
            session.Id,
            null,
            recipient.Id,
            MessageDirection.MediatorToParticipant,
            text,
            createdAt);

    private sealed class CapturingModelRuntime : IModelRuntime
    {
        public List<ConversationContext> Contexts { get; } = [];

        public Task<ModelResult> ProcessAsync(
            ConversationContext context,
            CancellationToken cancellationToken = default)
        {
            Contexts.Add(context);
            return Task.FromResult(new ModelResult([]));
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
            new SqliteConversationStoreOptions(Path, "integration-test-key"));

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
