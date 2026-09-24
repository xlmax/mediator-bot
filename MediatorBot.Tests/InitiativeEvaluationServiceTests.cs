using MediatorBot.Core;
using MediatorBot.Infrastructure;

namespace MediatorBot.Tests;

public sealed class InitiativeEvaluationServiceTests
{
    [Fact]
    public async Task DueEvaluation_PersistsLiveContactDecision()
    {
        var fixture = await CreateFixtureAsync(new InitiativeProposal(
            RelationshipPhase.CoolingDown,
            InitiativeConfidence.Medium,
            InitiativeDecisionKind.ContactParticipant,
            TargetParticipantId: Guid.Empty,
            InitiativeIntent.CheckIn,
            InitiativeReasonCode.RecentConflict,
            "После конфликта прошло достаточно времени для короткой проверки.",
            "Как тебе сейчас?",
            null,
            120));
        fixture.Runtime.Transform = proposal => proposal with
        {
            TargetParticipantId = fixture.Session.ParticipantA.Id
        };

        var decision = await fixture.Service.TryEvaluateAsync(
            fixture.Session.Id,
            fixture.Now);

        Assert.NotNull(decision);
        Assert.Equal(InitiativeDecisionStatus.PendingDelivery, decision.Status);
        Assert.Equal(fixture.Session.ParticipantA.Id, decision.TargetParticipantId);
        Assert.Equal(fixture.Now.AddHours(2), decision.NextEvaluationAt);
        Assert.Equal(
            decision.Id,
            (await fixture.Store.GetLatestDecisionAsync(fixture.Session.Id))?.Id);
    }

    [Fact]
    public async Task EvaluationDuringMoscowQuietHours_DoesNotCallModel()
    {
        var fixture = await CreateFixtureAsync(DefaultNoAction());
        var quietTime = new DateTimeOffset(2026, 9, 24, 20, 0, 0, TimeSpan.Zero);

        var decision = await fixture.Service.TryEvaluateAsync(
            fixture.Session.Id,
            quietTime);

        Assert.Null(decision);
        Assert.Equal(0, fixture.Runtime.CallCount);
    }

    [Fact]
    public async Task NewPendingTurnDuringModelCall_DiscardsStaleDecision()
    {
        var fixture = await CreateFixtureAsync(DefaultNoAction());
        fixture.Runtime.BeforeReturn = async () =>
        {
            var turn = new PendingExternalTurn(
                Guid.NewGuid(),
                fixture.Session.Id,
                fixture.Session.ParticipantA.Id,
                "test",
                "new-turn",
                1,
                "1",
                "Новое сообщение",
                fixture.Now);
            Assert.True(await fixture.Store.TryEnqueueAsync(turn));
        };

        var decision = await fixture.Service.TryEvaluateAsync(
            fixture.Session.Id,
            fixture.Now);

        Assert.Null(decision);
        Assert.Null(await fixture.Store.GetLatestDecisionAsync(fixture.Session.Id));
    }

    [Fact]
    public async Task ExplicitTemporarySpace_PersistsParticipantPause()
    {
        var fixture = await CreateFixtureAsync(DefaultNoAction());
        fixture.Runtime.Transform = proposal => proposal with
        {
            ReasonCode = InitiativeReasonCode.RequestedSpace,
            PauseParticipantId = fixture.Session.ParticipantB.Id,
            PauseForMinutes = 360
        };

        var decision = await fixture.Service.TryEvaluateAsync(
            fixture.Session.Id,
            fixture.Now);
        var preferences = await fixture.Store.GetParticipantPreferencesAsync(
            fixture.Session);
        var participantB = preferences.Single(item =>
            item.ParticipantId == fixture.Session.ParticipantB.Id);

        Assert.NotNull(decision);
        Assert.Equal(fixture.Session.ParticipantB.Id, decision.PauseParticipantId);
        Assert.Equal(fixture.Now.AddHours(6), decision.PauseUntil);
        Assert.Equal(fixture.Now.AddHours(6), participantB.PauseUntil);
        Assert.False(participantB.AllowsContact(fixture.Now.AddHours(1)));
        Assert.True(participantB.AllowsContact(fixture.Now.AddHours(7)));
    }

    [Fact]
    public async Task SameSpaceRequest_DoesNotExtendPauseRepeatedly()
    {
        var fixture = await CreateFixtureAsync(DefaultNoAction() with
        {
            ReasonCode = InitiativeReasonCode.RequestedSpace,
            ReevaluateAfterMinutes = 30,
            PauseForMinutes = 60
        });
        fixture.Runtime.Transform = proposal => proposal with
        {
            PauseParticipantId = fixture.Session.ParticipantA.Id
        };

        await fixture.Service.TryEvaluateAsync(fixture.Session.Id, fixture.Now);
        await fixture.Service.TryEvaluateAsync(
            fixture.Session.Id,
            fixture.Now.AddMinutes(31));
        var preference = (await fixture.Store.GetParticipantPreferencesAsync(
            fixture.Session)).Single(item =>
            item.ParticipantId == fixture.Session.ParticipantA.Id);

        Assert.Equal(fixture.Now.AddMinutes(60), preference.PauseUntil);
        Assert.Equal(2, fixture.Runtime.CallCount);
    }

    [Fact]
    public async Task UnchangedContextBeforeNextEvaluation_SkipsModelCall()
    {
        var fixture = await CreateFixtureAsync(DefaultNoAction());
        var first = await fixture.Service.TryEvaluateAsync(
            fixture.Session.Id,
            fixture.Now);
        Assert.NotNull(first);

        var second = await fixture.Service.TryEvaluateAsync(
            fixture.Session.Id,
            fixture.Now.AddMinutes(30));

        Assert.Null(second);
        Assert.Equal(1, fixture.Runtime.CallCount);
    }

    private static InitiativeProposal DefaultNoAction() => new(
        RelationshipPhase.Calm,
        InitiativeConfidence.Medium,
        InitiativeDecisionKind.NoAction,
        null,
        InitiativeIntent.Observe,
        InitiativeReasonCode.NoUsefulAction,
        "Сейчас нет достаточной пользы от контакта.",
        null,
        null,
        180);

    private static async Task<Fixture> CreateFixtureAsync(InitiativeProposal proposal)
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
            "Недавний контекст",
            now.AddHours(-2)));
        var options = new InitiativeOptions
        {
            Enabled = true,
            ShadowMode = false,
            MinimumQuietPeriod = TimeSpan.FromMinutes(30),
            TimeZoneId = "Europe/Moscow",
            QuietHoursStartHour = 22,
            QuietHoursEndHour = 9
        };
        var runtime = new StubInitiativeRuntime(proposal);
        var contextBuilder = new InitiativeContextBuilder(
            store,
            store,
            store,
            store,
            options);
        var service = new InitiativeEvaluationService(
            contextBuilder,
            runtime,
            store,
            store,
            new SessionTurnCoordinator(),
            options);
        return new Fixture(session, store, runtime, service, now);
    }

    private sealed record Fixture(
        Session Session,
        InMemoryConversationStore Store,
        StubInitiativeRuntime Runtime,
        InitiativeEvaluationService Service,
        DateTimeOffset Now);

    private sealed class StubInitiativeRuntime(InitiativeProposal proposal)
        : IInitiativeRuntime
    {
        public int CallCount { get; private set; }

        public Func<InitiativeProposal, InitiativeProposal>? Transform { get; set; }

        public Func<Task>? BeforeReturn { get; set; }

        public async Task<InitiativeProposal> EvaluateAsync(
            InitiativeContext context,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (BeforeReturn is not null)
            {
                await BeforeReturn();
            }

            return Transform?.Invoke(proposal) ?? proposal;
        }
    }
}
