using MediatorBot.Core;
using MediatorBot.Infrastructure;
using MediatorBot.Telegram;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediatorBot.Tests;

public sealed class TelegramAdapterTests
{
    private const long ParticipantAUserId = 10001;
    private const long ParticipantBUserId = 10002;

    [Fact]
    public async Task Registry_CreatesConfiguredSessionWhenItDoesNotExist()
    {
        var store = new InMemoryConversationStore();
        var sessionId = Guid.NewGuid();
        var registry = new TelegramParticipantRegistry(
            store,
            store,
            new TelegramAdapterOptions
            {
                SessionId = sessionId,
                ParticipantAUserId = ParticipantAUserId,
                ParticipantADisplayName = "Анна",
                ParticipantBUserId = ParticipantBUserId,
                ParticipantBDisplayName = "Борис",
                ModelDisplayName = "Fake"
            });

        await registry.InitializeAsync();

        var session = await store.GetSessionAsync(sessionId);
        Assert.NotNull(session);
        Assert.NotEqual(session.ParticipantA.Id, session.ParticipantB.Id);
        Assert.Equal("Анна", session.ParticipantA.DisplayName);
        Assert.Equal("Борис", session.ParticipantB.DisplayName);
    }

    [Fact]
    public async Task Registry_RejectsChangedAccountBindingsForExistingSession()
    {
        var store = new InMemoryConversationStore();
        var sessionId = Guid.NewGuid();
        var initialRegistry = new TelegramParticipantRegistry(
            store,
            store,
            new TelegramAdapterOptions
            {
                SessionId = sessionId,
                ParticipantAUserId = ParticipantAUserId,
                ParticipantBUserId = ParticipantBUserId,
                ModelDisplayName = "Fake"
            });
        await initialRegistry.InitializeAsync();

        var changedRegistry = new TelegramParticipantRegistry(
            store,
            store,
            new TelegramAdapterOptions
            {
                SessionId = sessionId,
                ParticipantAUserId = ParticipantBUserId,
                ParticipantBUserId = ParticipantAUserId,
                ModelDisplayName = "Fake"
            });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            changedRegistry.InitializeAsync());
    }

    [Fact]
    public async Task Registry_UpdatesDisplayNamesWithoutChangingParticipantIdentity()
    {
        var participantA = new Participant(Guid.NewGuid(), "A");
        var participantB = new Participant(Guid.NewGuid(), "B");
        var session = new Session(Guid.NewGuid(), participantA, participantB);
        var store = new InMemoryConversationStore([session]);
        var registry = new TelegramParticipantRegistry(
            store,
            store,
            new TelegramAdapterOptions
            {
                SessionId = session.Id,
                ParticipantAUserId = ParticipantAUserId,
                ParticipantADisplayName = "Анна",
                ParticipantBUserId = ParticipantBUserId,
                ParticipantBDisplayName = "Борис",
                ModelDisplayName = "Fake"
            });

        await registry.InitializeAsync();

        var updatedSession = await store.GetSessionAsync(session.Id);
        Assert.NotNull(updatedSession);
        Assert.Equal(participantA.Id, updatedSession.ParticipantA.Id);
        Assert.Equal("Анна", updatedSession.ParticipantA.DisplayName);
        Assert.Equal(participantB.Id, updatedSession.ParticipantB.Id);
        Assert.Equal("Борис", updatedSession.ParticipantB.DisplayName);
    }

    [Fact]
    public async Task Registry_RejectsInvalidConfiguredDisplayName()
    {
        var store = new InMemoryConversationStore();
        var registry = new TelegramParticipantRegistry(
            store,
            store,
            new TelegramAdapterOptions
            {
                SessionId = Guid.NewGuid(),
                ParticipantAUserId = ParticipantAUserId,
                ParticipantADisplayName = new string('а', 101),
                ParticipantBUserId = ParticipantBUserId,
                ParticipantBDisplayName = "Борис",
                ModelDisplayName = "Fake"
            });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.InitializeAsync());
    }

    [Fact]
    public async Task Registry_MapsConfiguredTelegramUsersToSessionParticipants()
    {
        var fixture = CreateFixture();

        var bindingA = await fixture.Registry.FindByTelegramUserIdAsync(
            ParticipantAUserId);
        var bindingB = await fixture.Registry.FindByTelegramUserIdAsync(
            ParticipantBUserId);

        Assert.NotNull(bindingA);
        Assert.NotNull(bindingB);
        Assert.Equal(fixture.Session.Id, bindingA.Session.Id);
        Assert.Equal(fixture.Session.ParticipantA.Id, bindingA.Participant.Id);
        Assert.Equal(fixture.Session.Id, bindingB.Session.Id);
        Assert.Equal(fixture.Session.ParticipantB.Id, bindingB.Participant.Id);
    }

    [Fact]
    public async Task UnknownTelegramUser_CannotSendMessageToSession()
    {
        var runtime = new RecordingModelRuntime();
        var fixture = CreateFixture(runtime);

        var result = await fixture.Processor.ProcessPrivateTextAsync(
            1,
            99999,
            "Попытка доступа");

        Assert.Equal(TelegramMessageProcessingStatus.UnknownUser, result);
        Assert.Empty(runtime.Contexts);
        Assert.Empty(await fixture.Store.GetHistoryAsync(fixture.Session.Id));
        Assert.Empty(fixture.Transport.Deliveries);
    }

    [Fact]
    public async Task DuplicateTelegramUpdate_IsIgnoredBeforePersistenceAndModelCall()
    {
        var runtime = new RecordingModelRuntime();
        var fixture = CreateFixture(runtime);

        var firstStatus = await fixture.Processor.ProcessPrivateTextAsync(
            2,
            ParticipantAUserId,
            "Первое получение");
        var duplicateStatus = await fixture.Processor.ProcessPrivateTextAsync(
            2,
            ParticipantAUserId,
            "Повторная доставка того же update");

        Assert.Equal(TelegramMessageProcessingStatus.Processed, firstStatus);
        Assert.Equal(TelegramMessageProcessingStatus.Duplicate, duplicateStatus);
        Assert.Single(runtime.Contexts);
        var incoming = Assert.Single(
            await fixture.Store.GetHistoryAsync(fixture.Session.Id));
        Assert.Equal("Первое получение", incoming.Text);
    }

    [Fact]
    public async Task UnknownTelegramUser_CannotUseSessionCommands()
    {
        var fixture = CreateFixture();
        var commands = new TelegramCommandService(
            fixture.Registry,
            fixture.Store,
            fixture.Store,
            fixture.Options);

        var responses = new[]
        {
            await commands.GetStartAsync(99999),
            await commands.GetStatusAsync(99999),
            await commands.GetHelpAsync(99999)
        };

        Assert.All(responses, response => Assert.False(response.IsAuthorized));
        Assert.All(
            responses,
            response => Assert.DoesNotContain(fixture.Session.Id.ToString(), response.Text));
    }

    [Fact]
    public async Task MessagesFromAAndB_UseTheirOwnParticipantsInSharedSession()
    {
        var runtime = new RecordingModelRuntime();
        var fixture = CreateFixture(runtime);

        await fixture.Processor.ProcessPrivateTextAsync(10, ParticipantAUserId, "От A");
        await fixture.Processor.ProcessPrivateTextAsync(11, ParticipantBUserId, "От B");

        Assert.Collection(
            runtime.Contexts,
            context =>
            {
                Assert.Equal(fixture.Session.Id, context.Session.Id);
                Assert.Equal(fixture.Session.ParticipantA.Id, context.Author.Id);
            },
            context =>
            {
                Assert.Equal(fixture.Session.Id, context.Session.Id);
                Assert.Equal(fixture.Session.ParticipantB.Id, context.Author.Id);
            });
    }

    [Fact]
    public async Task SendToParticipantA_IsDeliveredOnlyToA()
    {
        var fixture = CreateFixture();

        await DispatchAsync(
            fixture,
            20,
            ParticipantBUserId,
            [
                new SendToParticipant(
                    fixture.Session.ParticipantA.Id,
                    "Только для A",
                    DisclosureDecision.MediatorDisclosure)
            ]);

        var delivery = Assert.Single(fixture.Transport.Deliveries);
        Assert.Equal(ParticipantAUserId, delivery.TelegramUserId);
        Assert.Equal("Только для A", delivery.Text);
        var message = Assert.Single(await fixture.Store.GetHistoryAsync(fixture.Session.Id));
        Assert.Equal(fixture.Session.ParticipantA.Id, message.RecipientId);
    }

    [Fact]
    public async Task LongMediatorMessage_IsDeliveredInChunksAndRecordedAsOneLogicalMessage()
    {
        var fixture = CreateFixture();
        var text = new string('д', 8500);

        await DispatchAsync(
            fixture,
            20,
            ParticipantBUserId,
            [
                new SendToParticipant(
                    fixture.Session.ParticipantA.Id,
                    text,
                    DisclosureDecision.MediatorDisclosure)
            ]);

        Assert.Equal(3, fixture.Transport.Deliveries.Count);
        Assert.All(
            fixture.Transport.Deliveries,
            delivery => Assert.InRange(
                delivery.Text.Length,
                1,
                TelegramTextChunker.DefaultMaxChunkLength));
        Assert.Equal(
            text,
            string.Concat(fixture.Transport.Deliveries.Select(delivery => delivery.Text)));

        var history = await fixture.Store.GetHistoryAsync(fixture.Session.Id);
        Assert.Equal(text, Assert.Single(history).Text);
    }

    [Fact]
    public async Task SendToBoth_CreatesTwoSeparateDeliveriesAndHistoryMessages()
    {
        var fixture = CreateFixture();

        await DispatchAsync(
            fixture,
            21,
            ParticipantAUserId,
            [
                new SendToBoth(
                    "Для A",
                    "Для B",
                    DisclosureDecision.MediatorDisclosure)
            ]);

        Assert.Collection(
            fixture.Transport.Deliveries,
            delivery => Assert.Equal((ParticipantAUserId, "Для A"), delivery),
            delivery => Assert.Equal((ParticipantBUserId, "Для B"), delivery));
        var history = await fixture.Store.GetHistoryAsync(fixture.Session.Id);
        Assert.Collection(
            history,
            message => Assert.Equal(fixture.Session.ParticipantA.Id, message.RecipientId),
            message => Assert.Equal(fixture.Session.ParticipantB.Id, message.RecipientId));
    }

    [Fact]
    public async Task MediatedRequest_ClosesRequesterWaitAfterRespondentDeclines()
    {
        var fixture = CreateFixture();
        var requestId = Guid.NewGuid();
        await DispatchAsync(
            fixture,
            22,
            ParticipantAUserId,
            [
                new OpenMediatedRequest(
                    requestId,
                    fixture.Session.ParticipantA.Id,
                    fixture.Session.ParticipantB.Id,
                    "Уточнить текущие занятия B",
                    "Я уточню, но ответ зависит от согласия B.",
                    "A спрашивает, что ты делаешь. Можно не отвечать.",
                    DisclosureDecision.ExplicitTransfer)
            ]);

        var openRequest = Assert.Single(
            await fixture.Store.GetOpenAsync(fixture.Session.Id));
        Assert.Equal(requestId, openRequest.Id);
        Assert.Collection(
            fixture.Transport.Deliveries,
            toB => Assert.Equal(ParticipantBUserId, toB.TelegramUserId),
            toA => Assert.Equal(ParticipantAUserId, toA.TelegramUserId));

        await DispatchAsync(
            fixture,
            23,
            ParticipantBUserId,
            [
                new ResolveMediatedRequest(
                    requestId,
                    fixture.Session.ParticipantA.Id,
                    fixture.Session.ParticipantB.Id,
                    MediatedRequestOutcome.Declined,
                    "У меня нет ответа, который я могу тебе передать.",
                    null,
                    DisclosureDecision.MediatorDisclosure)
            ]);

        Assert.Empty(await fixture.Store.GetOpenAsync(fixture.Session.Id));
        Assert.Equal(3, fixture.Transport.Deliveries.Count);
        Assert.Equal(
            (ParticipantAUserId, "У меня нет ответа, который я могу тебе передать."),
            fixture.Transport.Deliveries[2]);
    }

    [Fact]
    public async Task NoAction_DoesNotSendOrCreateHistoryMessage()
    {
        var fixture = CreateFixture();

        await DispatchAsync(
            fixture,
            22,
            ParticipantAUserId,
            [new NoAction()]);

        Assert.Empty(fixture.Transport.Deliveries);
        Assert.Empty(await fixture.Store.GetHistoryAsync(fixture.Session.Id));
    }

    [Fact]
    public async Task FailedDelivery_IsNotIncludedInNextModelContext()
    {
        var runtime = new RecordingModelRuntime();
        var fixture = CreateFixture(runtime);
        fixture.Transport.FailForTelegramUserId = ParticipantAUserId;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DispatchAsync(
                fixture,
                23,
                ParticipantBUserId,
                [new SendToParticipant(
                    fixture.Session.ParticipantA.Id,
                    "Недоставленный ответ",
                    DisclosureDecision.MediatorDisclosure)]));

        fixture.Transport.FailForTelegramUserId = null;
        await fixture.Processor.ProcessPrivateTextAsync(
            24,
            ParticipantBUserId,
            "Следующее входящее");

        var context = Assert.Single(runtime.Contexts);
        Assert.Single(context.History);
        Assert.DoesNotContain(
            context.History,
            message => message.Text == "Недоставленный ответ");
    }

    [Fact]
    public async Task ModelProtocolFailure_ReturnsControlledStatusWithoutDelivery()
    {
        var fixture = CreateFixture(new ProtocolFailureModelRuntime());

        var status = await fixture.Processor.ProcessPrivateTextAsync(
            29,
            ParticipantAUserId,
            "Сообщение с ошибкой протокола");

        Assert.Equal(TelegramMessageProcessingStatus.ModelProtocolFailure, status);
        Assert.Empty(fixture.Transport.Deliveries);
        var incoming = Assert.Single(
            await fixture.Store.GetHistoryAsync(fixture.Session.Id));
        Assert.Equal(MessageDirection.ParticipantToMediator, incoming.Direction);
    }

    [Fact]
    public async Task ProviderFailure_ReturnsUnavailableStatusWithoutDelivery()
    {
        var fixture = CreateFixture(new ProviderFailureModelRuntime());

        var status = await fixture.Processor.ProcessPrivateTextAsync(
            30,
            ParticipantAUserId,
            "Сообщение при недоступном провайдере");

        Assert.Equal(TelegramMessageProcessingStatus.ModelUnavailable, status);
        var duplicateStatus = await fixture.Processor.ProcessPrivateTextAsync(
            30,
            ParticipantAUserId,
            "Повторная доставка после ошибки провайдера");

        Assert.Equal(TelegramMessageProcessingStatus.Duplicate, duplicateStatus);
        Assert.Empty(fixture.Transport.Deliveries);
        var incoming = Assert.Single(
            await fixture.Store.GetHistoryAsync(fixture.Session.Id));
        Assert.Equal(MessageDirection.ParticipantToMediator, incoming.Direction);
    }

    [Fact]
    public async Task MessageWaitingForCompaction_IsNotifiedThenUsesCompactedContext()
    {
        var runtime = new RecordingModelRuntime();
        var summaryGenerator = new BlockingSummaryGenerator();
        var compactionOptions = new ConversationCompactionOptions
        {
            Enabled = true,
            TriggerMessageCount = 4,
            TriggerHistoryCharacters = 10_000,
            RetainRecentMessageCount = 2,
            RetainRecentCharacters = 5_000,
            MaxSummaryCharacters = 500,
            MaxOutputTokens = 100,
            OperationTimeout = TimeSpan.FromSeconds(5),
            RetryDelay = TimeSpan.FromSeconds(1)
        };
        var fixture = CreateFixture(
            runtime,
            compactionOptions: compactionOptions,
            summaryGenerator: summaryGenerator);
        var oldBaseTime = DateTimeOffset.UtcNow.AddMinutes(-1);
        foreach (var index in Enumerable.Range(1, 4))
        {
            await fixture.Store.SaveMessageAsync(new Message(
                Guid.NewGuid(),
                fixture.Session.Id,
                fixture.Session.ParticipantA.Id,
                null,
                MessageDirection.ParticipantToMediator,
                $"old-{index}",
                oldBaseTime.AddSeconds(index)));
        }

        var processing = fixture.Processor.ProcessPrivateTextAsync(
            29,
            ParticipantAUserId,
            "Новое после сжатия");
        await summaryGenerator.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(fixture.Transport.Deliveries, delivery =>
            delivery.Text.Contains("отвечу чуть позже", StringComparison.Ordinal));

        summaryGenerator.Release.TrySetResult();
        Assert.Equal(
            TelegramMessageProcessingStatus.Processed,
            await processing.WaitAsync(TimeSpan.FromSeconds(5)));

        var context = Assert.Single(runtime.Contexts);
        Assert.NotNull(context.Summary);
        Assert.Equal("private-a", context.Summary.Content.PrivateContextFromParticipantA);
        Assert.Equal(
            ["old-3", "old-4", "Новое после сжатия"],
            context.History.Select(message => message.Text));
        Assert.Equal(2, fixture.Transport.Deliveries.Count);
        Assert.Contains(fixture.Transport.Deliveries, delivery =>
            delivery.Text == "Готово, продолжаю с вашим сообщением.");
        Assert.DoesNotContain(
            await fixture.Store.GetHistoryAsync(fixture.Session.Id),
            message => message.Text.Contains("накопившийся контекст", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConcurrentMessagesInSameSession_AreSerializedThroughDelivery()
    {
        var runtime = new DelayedFirstTurnModelRuntime();
        var fixture = CreateFixture(runtime);

        var firstTurn = fixture.Processor.ProcessPrivateTextAsync(
            30,
            ParticipantAUserId,
            "Первое входящее");
        await runtime.FirstTurnEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var secondTurn = fixture.Processor.ProcessPrivateTextAsync(
            31,
            ParticipantBUserId,
            "Второе входящее");
        await Task.Delay(100);
        Assert.Equal(1, runtime.CallCount);

        runtime.ReleaseFirstTurn.TrySetResult();
        await Task.WhenAll(firstTurn, secondTurn);

        Assert.Equal(2, runtime.CallCount);
        var secondContext = runtime.Contexts[1];
        Assert.Collection(
            secondContext.History,
            message => Assert.Equal("Первое входящее", message.Text),
            message =>
            {
                Assert.Equal("Ответ первого turn", message.Text);
                Assert.Equal(MessageDirection.MediatorToParticipant, message.Direction);
            },
            message => Assert.Equal("Второе входящее", message.Text));

        var storedHistory = await fixture.Store.GetHistoryAsync(fixture.Session.Id);
        Assert.Equal(
            ["Первое входящее", "Ответ первого turn", "Второе входящее"],
            storedHistory.Select(message => message.Text));
    }

    [Fact]
    public async Task SendToBothPartialFailure_RecordsAAndReleasesSessionForNextTurn()
    {
        var runtime = new SendToBothModelRuntime();
        var fixture = CreateFixture(runtime);
        fixture.Transport.FailForTelegramUserId = ParticipantBUserId;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Processor.ProcessPrivateTextAsync(
                32,
                ParticipantAUserId,
                "Первый turn"));

        var historyAfterFailure = await fixture.Store.GetHistoryAsync(fixture.Session.Id);
        Assert.Equal(2, historyAfterFailure.Count);
        Assert.Equal("Для A", historyAfterFailure[1].Text);
        Assert.Equal(fixture.Session.ParticipantA.Id, historyAfterFailure[1].RecipientId);
        Assert.DoesNotContain(
            historyAfterFailure,
            message => message.RecipientId == fixture.Session.ParticipantB.Id);

        fixture.Transport.FailForTelegramUserId = null;
        var status = await fixture.Processor.ProcessPrivateTextAsync(
            33,
            ParticipantBUserId,
            "Следующий turn").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(TelegramMessageProcessingStatus.Processed, status);
        Assert.Equal(2, runtime.CallCount);
        Assert.Equal(1, fixture.Transport.Deliveries.Count(delivery =>
            delivery == (ParticipantAUserId, "Для A")));
        Assert.Equal(1, fixture.Transport.Deliveries.Count(delivery =>
            delivery == (ParticipantBUserId, "Для B")));
        var finalHistory = await fixture.Store.GetHistoryAsync(fixture.Session.Id);
        Assert.Equal(
            ["Первый turn", "Для A", "Для B", "Следующий turn"],
            finalHistory.Select(message => message.Text));
    }

    [Fact]
    public async Task OpenRequestRecovery_DoesNotDuplicateRespondentDelivery()
    {
        var requestId = Guid.NewGuid();
        var runtime = new OpenRequestThenNoActionRuntime(requestId);
        var fixture = CreateFixture(runtime);
        fixture.Transport.FailForTelegramUserId = ParticipantAUserId;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Processor.ProcessPrivateTextAsync(
                34,
                ParticipantAUserId,
                "Открыть запрос"));
        var open = Assert.Single(await fixture.Store.GetOpenAsync(fixture.Session.Id));
        Assert.Equal(MediatedRequestStatus.AwaitingResponse, open.Status);
        Assert.Equal(1, fixture.Transport.Deliveries.Count(delivery =>
            delivery == (ParticipantBUserId, "Вопрос для B")));

        fixture.Transport.FailForTelegramUserId = null;
        Assert.Equal(
            TelegramMessageProcessingStatus.Processed,
            await fixture.Processor.ProcessPrivateTextAsync(
                35,
                ParticipantBUserId,
                "Следующий turn").WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(2, runtime.CallCount);
        Assert.Equal(1, fixture.Transport.Deliveries.Count(delivery =>
            delivery == (ParticipantBUserId, "Вопрос для B")));
        Assert.Equal(1, fixture.Transport.Deliveries.Count(delivery =>
            delivery == (ParticipantAUserId, "Запрос открыт")));
    }

    [Fact]
    public async Task DeliveryTimeout_ReleasesSessionForNextTurn()
    {
        var fixture = CreateFixture(
            new SendToParticipantModelRuntime(),
            TimeSpan.FromMilliseconds(50));
        fixture.Transport.HangForTelegramUserId = ParticipantAUserId;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Processor.ProcessPrivateTextAsync(
                34,
                ParticipantAUserId,
                "Turn с зависшей доставкой"));

        fixture.Transport.HangForTelegramUserId = null;
        var status = await fixture.Processor.ProcessPrivateTextAsync(
            35,
            ParticipantBUserId,
            "Turn после timeout").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(TelegramMessageProcessingStatus.Processed, status);
        Assert.Contains(
            fixture.Transport.Deliveries,
            delivery => delivery.TelegramUserId == ParticipantBUserId);
    }

    [Fact]
    public async Task CallerCancellationAfterDelivery_DoesNotPreventHistoryRecording()
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = CreateFixture(new SendToParticipantModelRuntime());
        fixture.Transport.AfterSuccessfulSend = cancellation.Cancel;

        var status = await fixture.Processor.ProcessPrivateTextAsync(
            36,
            ParticipantAUserId,
            "Turn с отменой после доставки",
            cancellation.Token);

        Assert.Equal(TelegramMessageProcessingStatus.Processed, status);
        var history = await fixture.Store.GetHistoryAsync(fixture.Session.Id);
        Assert.Collection(
            history,
            incoming => Assert.Equal(
                MessageDirection.ParticipantToMediator,
                incoming.Direction),
            outgoing => Assert.Equal(
                MessageDirection.MediatorToParticipant,
                outgoing.Direction));
    }

    [Fact]
    public async Task RecordingTimeout_OccursAfterSuccessfulDelivery()
    {
        var fixture = CreateFixture(
            new SendToParticipantModelRuntime(),
            deliveryRecordingTimeout: TimeSpan.FromMilliseconds(30),
            turnExecutionStore: new HangingTurnExecutionStore());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Processor.ProcessPrivateTextAsync(
                37,
                ParticipantAUserId,
                "Turn с зависшей записью"));

        Assert.Single(fixture.Transport.Deliveries);
        var incoming = Assert.Single(
            await fixture.Store.GetHistoryAsync(fixture.Session.Id));
        Assert.Equal(MessageDirection.ParticipantToMediator, incoming.Direction);
    }

    [Fact]
    public async Task SessionTurnCoordinator_AllowsDifferentSessionsInParallel()
    {
        var coordinator = new SessionTurnCoordinator();
        var bothEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredCount = 0;

        async Task<int> Turn(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref enteredCount) == 2)
            {
                bothEntered.TrySetResult();
            }

            await release.Task.WaitAsync(cancellationToken);
            return 1;
        }

        var first = coordinator.ExecuteAsync(Guid.NewGuid(), Turn);
        var second = coordinator.ExecuteAsync(Guid.NewGuid(), Turn);
        await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.TrySetResult();

        await Task.WhenAll(first, second);
        Assert.Equal(2, enteredCount);
    }

    [Fact]
    public async Task SessionTurnCoordinator_ReleasesSessionAfterException()
    {
        var coordinator = new SessionTurnCoordinator();
        var sessionId = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ExecuteAsync<int>(
                sessionId,
                _ => throw new InvalidOperationException("Expected failure.")));

        var result = await coordinator.ExecuteAsync(
            sessionId,
            _ => Task.FromResult(42)).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(42, result);
    }

    private static async Task DispatchAsync(
        TelegramFixture fixture,
        long updateId,
        long telegramUserId,
        IReadOnlyList<MediatorAction> actions)
    {
        var participant = telegramUserId == ParticipantAUserId
            ? fixture.Session.ParticipantA
            : fixture.Session.ParticipantB;
        var turn = new PendingExternalTurn(
            Guid.NewGuid(),
            fixture.Session.Id,
            participant.Id,
            "telegram",
            updateId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            updateId,
            telegramUserId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            $"synthetic-{updateId}",
            DateTimeOffset.UtcNow);
        Assert.True(await fixture.Store.TryEnqueueAsync(turn));
        try
        {
            await fixture.Dispatcher.DispatchAsync(turn, fixture.Session, actions);
        }
        finally
        {
            await fixture.Store.CompleteAsync(fixture.Session.Id, turn.Id);
        }
    }

    private static TelegramFixture CreateFixture(
        IModelRuntime? runtime = null,
        TimeSpan? deliveryTimeout = null,
        TimeSpan? deliveryRecordingTimeout = null,
        ITurnExecutionStore? turnExecutionStore = null,
        ConversationCompactionOptions? compactionOptions = null,
        IConversationSummaryGenerator? summaryGenerator = null)
    {
        var participantA = new Participant(Guid.NewGuid(), "A");
        var participantB = new Participant(Guid.NewGuid(), "B");
        var session = new Session(Guid.NewGuid(), participantA, participantB);
        var store = new InMemoryConversationStore([session]);
        var options = new TelegramAdapterOptions
        {
            SessionId = session.Id,
            ParticipantAUserId = ParticipantAUserId,
            ParticipantBUserId = ParticipantBUserId,
            ModelDisplayName = "Fake",
            DeliveryTimeout = deliveryTimeout ?? TimeSpan.FromSeconds(30),
            DeliveryRecordingTimeout = deliveryRecordingTimeout ?? TimeSpan.FromSeconds(10)
        };
        var registry = new TelegramParticipantRegistry(store, store, options);
        var transport = new RecordingTelegramTransport();
        var effectiveTurnExecutionStore = turnExecutionStore ?? store;
        var dispatcher = new TelegramMediatorActionDispatcher(
            registry,
            transport,
            effectiveTurnExecutionStore,
            store,
            new TelegramTextChunker(),
            options,
            NullLogger<TelegramMediatorActionDispatcher>.Instance);
        var modelRuntime = runtime ?? new RecordingModelRuntime();
        var mediationService = new MediationService(
            store,
            modelRuntime,
            new ConversationContextBuilder(store, store, 100));
        var effectiveCompactionOptions = compactionOptions ??
            new ConversationCompactionOptions
            {
                Enabled = false
            };
        var compactionService = new ConversationCompactionService(
            store,
            store,
            summaryGenerator ?? new DisabledConversationSummaryGenerator(),
            effectiveCompactionOptions);
        var queuedTurnProcessor = new TelegramQueuedTurnProcessor(
            registry,
            mediationService,
            dispatcher,
            effectiveTurnExecutionStore,
            new MediatorActionSerializer(),
            options,
            NullLogger<TelegramQueuedTurnProcessor>.Instance);
        var workQueue = new TelegramSessionWorkQueue(
            new SessionTurnCoordinator(),
            compactionService,
            store,
            queuedTurnProcessor,
            transport,
            options,
            effectiveCompactionOptions,
            NullLogger<TelegramSessionWorkQueue>.Instance);
        var processor = new TelegramMessageProcessor(
            registry,
            store,
            workQueue,
            NullLogger<TelegramMessageProcessor>.Instance);

        return new TelegramFixture(
            session,
            store,
            options,
            registry,
            transport,
            dispatcher,
            processor);
    }

    private sealed record TelegramFixture(
        Session Session,
        InMemoryConversationStore Store,
        TelegramAdapterOptions Options,
        TelegramParticipantRegistry Registry,
        RecordingTelegramTransport Transport,
        TelegramMediatorActionDispatcher Dispatcher,
        TelegramMessageProcessor Processor);

    private sealed class RecordingModelRuntime : IModelRuntime
    {
        public List<ConversationContext> Contexts { get; } = [];

        public Task<ModelResult> ProcessAsync(
            ConversationContext context,
            CancellationToken cancellationToken = default)
        {
            Contexts.Add(context);
            return Task.FromResult(new ModelResult([new NoAction()]));
        }
    }

    private sealed class ProtocolFailureModelRuntime : IModelRuntime
    {
        public Task<ModelResult> ProcessAsync(
            ConversationContext context,
            CancellationToken cancellationToken = default) =>
            throw new OpenAiProtocolException("Simulated protocol failure.");
    }

    private sealed class ProviderFailureModelRuntime : IModelRuntime
    {
        public Task<ModelResult> ProcessAsync(
            ConversationContext context,
            CancellationToken cancellationToken = default) =>
            throw new OpenAiProviderException(429);
    }

    private sealed class DelayedFirstTurnModelRuntime : IModelRuntime
    {
        private int _callCount;

        public TaskCompletionSource FirstTurnEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstTurn { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public List<ConversationContext> Contexts { get; } = [];

        public int CallCount => Volatile.Read(ref _callCount);

        public async Task<ModelResult> ProcessAsync(
            ConversationContext context,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _callCount);
            Contexts.Add(context);
            if (call == 1)
            {
                FirstTurnEntered.TrySetResult();
                await ReleaseFirstTurn.Task.WaitAsync(cancellationToken);
                return new ModelResult(
                    [
                        new SendToParticipant(
                            context.Author.Id,
                            "Ответ первого turn",
                            DisclosureDecision.PrivateResponse)
                    ]);
            }

            return new ModelResult([new NoAction()]);
        }
    }

    private sealed class OpenRequestThenNoActionRuntime(Guid requestId) : IModelRuntime
    {
        public int CallCount { get; private set; }

        public Task<ModelResult> ProcessAsync(
            ConversationContext context,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult<ModelResult>(CallCount == 1
                ? new([
                    new OpenMediatedRequest(
                        requestId,
                        context.Session.ParticipantA.Id,
                        context.Session.ParticipantB.Id,
                        "summary",
                        "Запрос открыт",
                        "Вопрос для B",
                        DisclosureDecision.ExplicitTransfer)
                ])
                : new([new NoAction()]));
        }
    }

    private sealed class SendToParticipantModelRuntime : IModelRuntime
    {
        public Task<ModelResult> ProcessAsync(
            ConversationContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ModelResult>(new(
                [
                    new SendToParticipant(
                        context.Author.Id,
                        "Ответ",
                        DisclosureDecision.PrivateResponse)
                ]));
    }

    private sealed class SendToBothModelRuntime : IModelRuntime
    {
        public int CallCount { get; private set; }

        public Task<ModelResult> ProcessAsync(
            ConversationContext context,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult<ModelResult>(CallCount == 1
                ? new([
                    new SendToBoth(
                        "Для A",
                        "Для B",
                        DisclosureDecision.MediatorDisclosure)
                ])
                : new([new NoAction()]));
        }
    }

    private sealed class BlockingSummaryGenerator : IConversationSummaryGenerator
    {
        public TaskCompletionSource Entered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ConversationSummaryContent> GenerateAsync(
            Session session,
            ConversationSummaryContent? previousSummary,
            IReadOnlyList<SequencedMessage> messages,
            int maxSummaryCharacters,
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new ConversationSummaryContent(
                "private-a",
                "",
                "",
                "");
        }
    }

    private sealed class HangingTurnExecutionStore : ITurnExecutionStore
    {
        public Task<string?> GetModelResultAsync(
            Guid sessionId,
            Guid turnId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task SaveModelResultAsync(
            Guid sessionId,
            Guid turnId,
            string modelResultJson,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<TurnDelivery>> EnsureDeliveryPlanAsync(
            TurnDeliveryPlan plan,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TurnDelivery>>(
                plan.Chunks.Select((text, index) => new TurnDelivery(
                    Guid.NewGuid(),
                    plan.TurnId,
                    plan.SessionId,
                    plan.ParticipantId,
                    Guid.NewGuid(),
                    plan.DeliveryKey,
                    index + 1,
                    plan.Chunks.Count,
                    text,
                    plan.ActionType,
                    plan.DisclosureDecision,
                    TurnDeliveryStatus.Pending,
                    plan.CreatedAt)).ToArray());

        public Task MarkDeliveryAttemptingAsync(
            Guid sessionId,
            Guid turnId,
            Guid deliveryId,
            DateTimeOffset attemptedAt,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RecordDeliveryAsync(
            Guid sessionId,
            Guid turnId,
            Guid deliveryId,
            DateTimeOffset deliveredAt,
            CancellationToken cancellationToken = default) =>
            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private sealed class RecordingTelegramTransport : ITelegramMessageTransport
    {
        public List<(long TelegramUserId, string Text)> Deliveries { get; } = [];

        public long? FailForTelegramUserId { get; set; }

        public long? HangForTelegramUserId { get; set; }

        public Action? AfterSuccessfulSend { get; set; }

        public Task SendTextMessageAsync(
            long telegramUserId,
            string text,
            CancellationToken cancellationToken = default)
        {
            if (telegramUserId == FailForTelegramUserId)
            {
                throw new InvalidOperationException("Simulated Telegram failure.");
            }

            if (telegramUserId == HangForTelegramUserId)
            {
                return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            Deliveries.Add((telegramUserId, text));
            AfterSuccessfulSend?.Invoke();
            return Task.CompletedTask;
        }
    }
}
