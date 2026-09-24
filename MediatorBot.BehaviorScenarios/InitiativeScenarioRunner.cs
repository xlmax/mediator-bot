using System.Text;
using MediatorBot.Core;

namespace MediatorBot.BehaviorScenarios;

internal sealed class InitiativeScenarioRunner(IInitiativeRuntime runtime)
{
    public async Task<string> RunAsync(
        string model,
        string transcriptDirectory,
        int? scenarioNumber = null,
        CancellationToken cancellationToken = default)
    {
        var scenarios = scenarioNumber is null
            ? InitiativeScenarioCatalog.All
            : InitiativeScenarioCatalog.All
                .Where(item => item.Number == scenarioNumber)
                .ToArray();
        if (scenarios.Count == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scenarioNumber));
        }

        var transcript = new StringBuilder();
        transcript.AppendLine("# Proactive initiative behavioural scenarios");
        transcript.AppendLine();
        transcript.AppendLine($"- UTC: {DateTimeOffset.UtcNow:O}");
        transcript.AppendLine($"- Model: `{model}`");
        transcript.AppendLine("- Все реплики синтетические; сообщения физически не отправляются.");
        transcript.AppendLine();
        foreach (var scenario in scenarios)
        {
            await RunScenarioAsync(scenario, transcript, cancellationToken);
        }

        var absoluteDirectory = Path.GetFullPath(
            transcriptDirectory,
            Directory.GetCurrentDirectory());
        Directory.CreateDirectory(absoluteDirectory);
        var safeModel = string.Concat(model.Select(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_'
                ? character
                : '-'));
        var path = Path.Combine(
            absoluteDirectory,
            $"proactive-initiative-{safeModel}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.md");
        await File.WriteAllTextAsync(
            path,
            transcript.ToString(),
            new UTF8Encoding(false),
            cancellationToken);
        return path;
    }

    private async Task RunScenarioAsync(
        InitiativeScenario scenario,
        StringBuilder transcript,
        CancellationToken cancellationToken)
    {
        var participantA = new Participant(Guid.NewGuid(), "Алексей");
        var participantB = new Participant(Guid.NewGuid(), "Светлана");
        var session = new Session(Guid.NewGuid(), participantA, participantB);
        var now = DateTimeOffset.UtcNow;
        var messages = scenario.Messages
            .Select(item => new Message(
                Guid.NewGuid(),
                session.Id,
                item.Role == ParticipantRole.A ? participantA.Id : participantB.Id,
                null,
                MessageDirection.ParticipantToMediator,
                item.Text,
                now - item.Age))
            .OrderBy(message => message.CreatedAt)
            .ToArray();
        var latest = messages[^1];
        var decisions = scenario.PriorDecision is null
            ? Array.Empty<InitiativeDecision>()
            : [CreatePriorDecision(scenario.PriorDecision, session, latest.Id, now)];
        var context = new InitiativeContext(
            session,
            messages,
            latest,
            now,
            decisions,
            [
                new(session.Id, participantA.Id, true, null, session.CreatedAt),
                new(session.Id, participantB.Id, true, null, session.CreatedAt)
            ],
            new Dictionary<Guid, int>
            {
                [participantA.Id] = 0,
                [participantB.Id] = 0
            });

        transcript.AppendLine($"## Scenario {scenario.Number} — {scenario.Name}");
        transcript.AppendLine();
        transcript.AppendLine($"**Ожидание:** {scenario.Expectation}");
        transcript.AppendLine();
        foreach (var message in messages)
        {
            var label = message.AuthorId == participantA.Id ? "A" : "B";
            transcript.AppendLine(
                $"- `{message.CreatedAt:O}` Participant {label}: {message.Text}");
        }

        transcript.AppendLine();
        try
        {
            var proposal = await runtime.EvaluateAsync(context, cancellationToken);
            transcript.AppendLine($"- Phase: `{proposal.Phase}` ({proposal.Confidence})");
            transcript.AppendLine($"- Decision: `{proposal.DecisionKind}`");
            transcript.AppendLine($"- Target: `{proposal.TargetParticipantId?.ToString("D") ?? "none"}`");
            transcript.AppendLine($"- Intent: `{proposal.Intent}`");
            transcript.AppendLine($"- Reason: `{proposal.ReasonCode}`");
            transcript.AppendLine($"- ReevaluateAfterMinutes: `{proposal.ReevaluateAfterMinutes}`");
            transcript.AppendLine($"- Rationale: {proposal.OperationalRationale}");
            if (proposal.TextForParticipantA is not null)
            {
                transcript.AppendLine();
                transcript.AppendLine("**Proposed for A:**");
                transcript.AppendLine();
                transcript.AppendLine($"> {proposal.TextForParticipantA}");
            }

            if (proposal.TextForParticipantB is not null)
            {
                transcript.AppendLine();
                transcript.AppendLine("**Proposed for B:**");
                transcript.AppendLine();
                transcript.AppendLine($"> {proposal.TextForParticipantB}");
            }

            if (proposal.PauseParticipantId is not null)
            {
                transcript.AppendLine();
                transcript.AppendLine(
                    $"- Contact pause: `{proposal.PauseParticipantId:D}` for " +
                    $"`{proposal.PauseForMinutes}` minutes");
            }
        }
        catch (Exception exception)
        {
            transcript.AppendLine($"**ERROR:** `{exception.GetType().Name}`");
        }

        transcript.AppendLine();
    }

    private static InitiativeDecision CreatePriorDecision(
        InitiativeScenarioPriorDecision prior,
        Session session,
        Guid observedMessageId,
        DateTimeOffset now)
    {
        var target = prior.Target == ParticipantRole.A
            ? session.ParticipantA
            : session.ParticipantB;
        var evaluatedAt = now - prior.Age;
        return new InitiativeDecision(
            Guid.NewGuid(),
            session.Id,
            observedMessageId,
            evaluatedAt,
            RelationshipPhase.CoolingDown,
            InitiativeConfidence.Medium,
            InitiativeDecisionKind.ContactParticipant,
            target.Id,
            prior.Intent,
            InitiativeReasonCode.RecentConflict,
            "Предыдущая инициативная проверка.",
            target.Id == session.ParticipantA.Id ? prior.ProposedText : null,
            target.Id == session.ParticipantB.Id ? prior.ProposedText : null,
            evaluatedAt.AddHours(4),
            prior.Status,
            DeliveredAt: prior.Status == InitiativeDecisionStatus.Delivered
                ? evaluatedAt
                : null);
    }
}
