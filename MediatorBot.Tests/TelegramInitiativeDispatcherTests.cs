using MediatorBot.Core;
using MediatorBot.Infrastructure;
using MediatorBot.Telegram;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediatorBot.Tests;

public sealed class TelegramInitiativeDispatcherTests
{
    [Fact]
    public async Task PendingLiveInitiative_IsDeliveredAndRecorded()
    {
        var fixture = await CreateFixtureAsync();
        var decision = await fixture.CreatePendingDecisionAsync(
            fixture.Session.ParticipantA,
            "Осталось ли желание спокойно вернуться к разговору?");

        await fixture.Dispatcher.DispatchPendingAsync(fixture.Session.Id, fixture.Now);

        var sent = Assert.Single(fixture.Transport.Deliveries);
        Assert.Equal(10001, sent.TelegramUserId);
        Assert.Contains("спокойно", sent.Text);
        var latest = await fixture.Store.GetLatestDecisionAsync(fixture.Session.Id);
        Assert.Equal(InitiativeDecisionStatus.Delivered, latest?.Status);
        Assert.Contains(
            await fixture.Store.GetHistoryAsync(fixture.Session.Id),
            message => message.Id != decision.ObservedParticipantMessageId &&
                       message.Direction == MessageDirection.MediatorToParticipant &&
                       message.RecipientId == fixture.Session.ParticipantA.Id);
    }

    [Fact]
    public async Task ParticipantOptOut_SuppressesPendingInitiative()
    {
        var fixture = await CreateFixtureAsync();
        await fixture.Store.SetParticipantEnabledAsync(
            fixture.Session.Id,
            fixture.Session.ParticipantA.Id,
            false,
            fixture.Now);
        await fixture.CreatePendingDecisionAsync(
            fixture.Session.ParticipantA,
            "Проверка состояния");

        await fixture.Dispatcher.DispatchPendingAsync(fixture.Session.Id, fixture.Now);

        Assert.Empty(fixture.Transport.Deliveries);
        Assert.Equal(
            InitiativeDecisionStatus.Suppressed,
            (await fixture.Store.GetLatestDecisionAsync(fixture.Session.Id))?.Status);
    }

    [Fact]
    public async Task ThirdContactWithin24Hours_IsSuppressed()
    {
        var fixture = await CreateFixtureAsync();
        for (var index = 0; index < 3; index++)
        {
            await fixture.CreatePendingDecisionAsync(
                fixture.Session.ParticipantA,
                $"Проверка {index + 1}");
            await fixture.Dispatcher.DispatchPendingAsync(
                fixture.Session.Id,
                fixture.Now.AddHours(index));
        }

        Assert.Equal(2, fixture.Transport.Deliveries.Count);
        Assert.Equal(
            InitiativeDecisionStatus.Suppressed,
            (await fixture.Store.GetLatestDecisionAsync(fixture.Session.Id))?.Status);
    }

    [Fact]
    public async Task PartialContactBothFailure_RecoversWithoutRepeatingDeliveredPart()
    {
        var fixture = await CreateFixtureAsync();
        await fixture.CreatePendingDecisionAsync(
            fixture.Session.ParticipantA,
            "Предыдущая проверка A");
        await fixture.Dispatcher.DispatchPendingAsync(fixture.Session.Id, fixture.Now);
        var observed = await fixture.Store.GetLatestParticipantMessageIdAsync(
            fixture.Session.Id) ?? throw new InvalidOperationException();
        var decision = new InitiativeDecision(
            Guid.NewGuid(),
            fixture.Session.Id,
            observed,
            fixture.Now,
            RelationshipPhase.RepairWindow,
            InitiativeConfidence.High,
            InitiativeDecisionKind.ContactBoth,
            null,
            InitiativeIntent.Bridge,
            InitiativeReasonCode.RepairOpportunity,
            "Оба участника показывают готовность к восстановлению контакта.",
            "Сообщение A",
            "Сообщение B",
            fixture.Now.AddHours(2),
            InitiativeDecisionStatus.PendingDelivery);
        var previous = await fixture.Store.GetLatestDecisionAsync(fixture.Session.Id);
        Assert.True(await fixture.Store.TrySaveDecisionAsync(decision, previous?.Id));
        fixture.Transport.FailForUserId = 10002;

        await fixture.Dispatcher.DispatchPendingAsync(fixture.Session.Id, fixture.Now);
        fixture.Transport.FailForUserId = null;
        await fixture.Dispatcher.DispatchPendingAsync(
            fixture.Session.Id,
            fixture.Now.AddMinutes(1));

        Assert.Equal(3, fixture.Transport.Deliveries.Count);
        Assert.Equal(2, fixture.Transport.Deliveries.Count(item =>
            item.TelegramUserId == 10001));
        Assert.Single(fixture.Transport.Deliveries, item => item.TelegramUserId == 10002);
        Assert.Equal(
            InitiativeDecisionStatus.Delivered,
            (await fixture.Store.GetLatestDecisionAsync(fixture.Session.Id))?.Status);
    }

    [Fact]
    public async Task RecoveredFullyDeliveredDecision_IsCompletedWithoutAnotherAttempt()
    {
        var fixture = await CreateFixtureAsync();
        var decision = await fixture.CreatePendingDecisionAsync(
            fixture.Session.ParticipantA,
            "Уже доставлено");
        var deliveries = await fixture.Store.EnsureInitiativeDeliveryPlanAsync(
            new InitiativeDeliveryPlan(
                decision.Id,
                decision.SessionId,
                fixture.Session.ParticipantA.Id,
                "participant-a",
                ["Уже доставлено"],
                decision.EvaluatedAt));
        await fixture.Store.MarkInitiativeDeliveryAttemptingAsync(
            decision.SessionId,
            decision.Id,
            deliveries[0].Id,
            fixture.Now);
        await fixture.Store.RecordInitiativeDeliveryAsync(
            decision.SessionId,
            decision.Id,
            deliveries[0].Id,
            fixture.Now);
        for (var index = 0; index < 3; index++)
        {
            await fixture.Store.BeginDecisionAttemptAsync(
                decision.SessionId,
                decision.Id);
        }

        await fixture.Store.SaveMessageAsync(new Message(
            Guid.NewGuid(),
            fixture.Session.Id,
            fixture.Session.ParticipantB.Id,
            null,
            MessageDirection.ParticipantToMediator,
            "Ответ после доставки до checkpoint",
            fixture.Now.AddMinutes(1)));
        await fixture.Dispatcher.DispatchPendingAsync(
            fixture.Session.Id,
            fixture.Now.AddMinutes(1));

        Assert.Empty(fixture.Transport.Deliveries);
        Assert.Equal(
            InitiativeDecisionStatus.Delivered,
            (await fixture.Store.GetLatestDecisionAsync(fixture.Session.Id))?.Status);
    }

    [Fact]
    public async Task NewParticipantMessage_SupersedesPendingInitiative()
    {
        var fixture = await CreateFixtureAsync();
        await fixture.CreatePendingDecisionAsync(
            fixture.Session.ParticipantA,
            "Устаревшее сообщение");
        await fixture.Store.SaveMessageAsync(new Message(
            Guid.NewGuid(),
            fixture.Session.Id,
            fixture.Session.ParticipantB.Id,
            null,
            MessageDirection.ParticipantToMediator,
            "Ситуация уже изменилась",
            fixture.Now));

        await fixture.Dispatcher.DispatchPendingAsync(fixture.Session.Id, fixture.Now);

        Assert.Empty(fixture.Transport.Deliveries);
        Assert.Equal(
            InitiativeDecisionStatus.Superseded,
            (await fixture.Store.GetLatestDecisionAsync(fixture.Session.Id))?.Status);
    }

    private static async Task<Fixture> CreateFixtureAsync()
    {
        var participantA = new Participant(Guid.NewGuid(), "A");
        var participantB = new Participant(Guid.NewGuid(), "B");
        var session = new Session(Guid.NewGuid(), participantA, participantB);
        var store = new InMemoryConversationStore([session]);
        var now = new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
        await store.SaveMessageAsync(new Message(
            Guid.NewGuid(),
            session.Id,
            participantA.Id,
            null,
            MessageDirection.ParticipantToMediator,
            "Исходный контекст",
            now.AddHours(-2)));
        var adapterOptions = new TelegramAdapterOptions
        {
            SessionId = session.Id,
            ParticipantAUserId = 10001,
            ParticipantBUserId = 10002,
            ModelDisplayName = "test"
        };
        var registry = new TelegramParticipantRegistry(store, store, adapterOptions);
        await registry.InitializeAsync();
        var initiativeOptions = new InitiativeOptions
        {
            Enabled = true,
            ShadowMode = false,
            TimeZoneId = "Europe/Moscow",
            QuietHoursStartHour = 22,
            QuietHoursEndHour = 9,
            MaxContactsPerParticipantPer24Hours = 2
        };
        var contextBuilder = new InitiativeContextBuilder(
            store,
            store,
            store,
            store,
            initiativeOptions);
        var evaluationService = new InitiativeEvaluationService(
            contextBuilder,
            new DisabledInitiativeRuntime(),
            store,
            store,
            new SessionTurnCoordinator(),
            initiativeOptions);
        var transport = new RecordingTransport();
        var dispatcher = new TelegramInitiativeDispatcher(
            registry,
            transport,
            store,
            store,
            store,
            store,
            new TelegramTextChunker(),
            adapterOptions,
            initiativeOptions,
            evaluationService,
            NullLogger<TelegramInitiativeDispatcher>.Instance);
        return new Fixture(session, store, transport, dispatcher, now);
    }

    private sealed record Fixture(
        Session Session,
        InMemoryConversationStore Store,
        RecordingTransport Transport,
        TelegramInitiativeDispatcher Dispatcher,
        DateTimeOffset Now)
    {
        public async Task<InitiativeDecision> CreatePendingDecisionAsync(
            Participant target,
            string text)
        {
            var observed = await Store.GetLatestParticipantMessageIdAsync(Session.Id)
                ?? throw new InvalidOperationException();
            var decision = new InitiativeDecision(
                Guid.NewGuid(),
                Session.Id,
                observed,
                Now.AddMinutes(-1),
                RelationshipPhase.CoolingDown,
                InitiativeConfidence.Medium,
                InitiativeDecisionKind.ContactParticipant,
                target.Id,
                InitiativeIntent.CheckIn,
                InitiativeReasonCode.RecentConflict,
                "Уместна короткая проверка.",
                target.Id == Session.ParticipantA.Id ? text : null,
                target.Id == Session.ParticipantB.Id ? text : null,
                Now.AddHours(2),
                InitiativeDecisionStatus.PendingDelivery);
            var latestDecision = await Store.GetLatestDecisionAsync(Session.Id);
            Assert.True(await Store.TrySaveDecisionAsync(decision, latestDecision?.Id));
            return decision;
        }
    }

    private sealed class RecordingTransport : ITelegramMessageTransport
    {
        public List<(long TelegramUserId, string Text)> Deliveries { get; } = [];

        public long? FailForUserId { get; set; }

        public Task SendTextMessageAsync(
            long telegramUserId,
            string text,
            CancellationToken cancellationToken = default)
        {
            if (FailForUserId == telegramUserId)
            {
                throw new InvalidOperationException("Injected delivery failure.");
            }

            Deliveries.Add((telegramUserId, text));
            return Task.CompletedTask;
        }
    }
}
