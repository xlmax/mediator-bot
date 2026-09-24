using MediatorBot.ConsoleApp;
using MediatorBot.Core;

namespace MediatorBot.Tests;

public sealed class InitiativeHtmlReportGeneratorTests
{
    [Fact]
    public void Generate_CreatesTimelineAndHtmlEncodesSensitiveText()
    {
        var participantA = new Participant(Guid.NewGuid(), "A");
        var participantB = new Participant(Guid.NewGuid(), "B");
        var session = new Session(Guid.NewGuid(), participantA, participantB);
        var decision = new InitiativeDecision(
            Guid.NewGuid(),
            session.Id,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            RelationshipPhase.RepairWindow,
            InitiativeConfidence.Medium,
            InitiativeDecisionKind.ContactParticipant,
            participantA.Id,
            InitiativeIntent.CheckIn,
            InitiativeReasonCode.RepairOpportunity,
            "Проверить <готовность>.",
            "Сообщение <A>",
            null,
            DateTimeOffset.UtcNow.AddHours(2),
            InitiativeDecisionStatus.Delivered,
            DeliveredAt: DateTimeOffset.UtcNow);

        var html = InitiativeHtmlReportGenerator.Generate(session, [decision]);

        Assert.Contains("Хронология инициатив", html);
        Assert.Contains("RepairWindow", html);
        Assert.Contains("Сообщение &lt;A&gt;", html);
        Assert.DoesNotContain("Сообщение <A>", html);
    }
}
