using System.Text;
using MediatorBot.Core;
using MediatorBot.Infrastructure;

namespace MediatorBot.BehaviorScenarios;

internal sealed class BehaviorScenarioRunner(IModelRuntime modelRuntime)
{
    public async Task<string> RunAsync(
        string model,
        string transcriptDirectory,
        int? scenarioNumber = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(transcriptDirectory);

        var transcript = new StringBuilder();
        var decisionCounts = Enum.GetValues<DisclosureDecision>()
            .ToDictionary(decision => decision, _ => 0);
        transcript.AppendLine("# Disclosure policy behavioural scenarios");
        transcript.AppendLine();
        transcript.AppendLine($"- UTC: {DateTimeOffset.UtcNow:O}");
        transcript.AppendLine($"- Model: `{model}`");
        var scenarios = scenarioNumber is null
            ? BehaviorScenarioCatalog.All
            : BehaviorScenarioCatalog.All
                .Where(scenario => scenario.Number == scenarioNumber)
                .ToArray();
        if (scenarios.Count == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(scenarioNumber),
                scenarioNumber,
                "Unknown behaviour scenario number.");
        }

        transcript.AppendLine("- Контекст каждого сценария изолирован от остальных.");
        transcript.AppendLine("- Все реплики синтетические; transcript предназначен для ручной оценки.");
        transcript.AppendLine();

        foreach (var scenario in scenarios)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RunScenarioAsync(
                scenario,
                transcript,
                decisionCounts,
                cancellationToken);
        }

        transcript.AppendLine("## Disclosure decision summary");
        transcript.AppendLine();
        foreach (var decision in Enum.GetValues<DisclosureDecision>())
        {
            transcript.AppendLine($"- `{decision}`: {decisionCounts[decision]}");
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
            $"disclosure-policy-{safeModel}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.md");
        await File.WriteAllTextAsync(
            path,
            transcript.ToString(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);
        return path;
    }

    private async Task RunScenarioAsync(
        BehaviorScenario scenario,
        StringBuilder transcript,
        IDictionary<DisclosureDecision, int> decisionCounts,
        CancellationToken cancellationToken)
    {
        var participantA = new Participant(Guid.NewGuid(), "Алексей");
        var participantB = new Participant(Guid.NewGuid(), "Светлана");
        var session = new Session(Guid.NewGuid(), participantA, participantB);
        var history = new List<Message>();
        var openRequests = new List<MediatedRequest>();
        var timestamp = DateTimeOffset.UtcNow;

        transcript.AppendLine($"## Scenario {scenario.Number} — {scenario.Name}");
        transcript.AppendLine();
        transcript.AppendLine($"**Ожидание:** {scenario.ExpectedBehavior}");
        transcript.AppendLine();

        for (var index = 0; index < scenario.Turns.Count; index++)
        {
            var turn = scenario.Turns[index];
            var author = turn.Author == ParticipantRole.A
                ? participantA
                : participantB;
            var incoming = new Message(
                Guid.NewGuid(),
                session.Id,
                author.Id,
                null,
                MessageDirection.ParticipantToMediator,
                turn.Text,
                timestamp.AddSeconds(index * 2));
            history.Add(incoming);

            transcript.AppendLine(
                $"### Turn {index + 1}: Participant {turn.Author} → Mediator");
            transcript.AppendLine();
            AppendQuote(transcript, turn.Text);
            transcript.AppendLine();

            try
            {
                var context = new ConversationContext(
                    session,
                    author,
                    history.ToArray(),
                    incoming)
                {
                    OpenMediatedRequests = openRequests.ToArray()
                };
                var result = await modelRuntime.ProcessAsync(
                    context,
                    cancellationToken);
                var action = AssertSingleAction(result);
                decisionCounts[action.DisclosureDecision]++;

                transcript.AppendLine(
                    $"**DisclosureDecision:** `{action.DisclosureDecision}`");
                transcript.AppendLine();
                AppendAction(
                    transcript,
                    action,
                    session,
                    history,
                    openRequests,
                    incoming.CreatedAt);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OpenAiProtocolException exception)
            {
                transcript.AppendLine(
                    $"**ERROR:** `{exception.GetType().Name}` / `{exception.Reason}`");
                if (exception.InnerException is not null)
                {
                    transcript.AppendLine(
                        $"**Technical cause:** `{exception.InnerException.Message}`");
                }
                transcript.AppendLine();
            }
            catch (Exception exception)
            {
                transcript.AppendLine($"**ERROR:** `{exception.GetType().Name}`");
                transcript.AppendLine();
            }
        }
    }

    private static MediatorAction AssertSingleAction(ModelResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(result.Actions);
        return result.Actions.Count == 1
            ? result.Actions[0]
            : throw new InvalidDataException(
                $"Expected one mediator action, received {result.Actions.Count}.");
    }

    private static void AppendAction(
        StringBuilder transcript,
        MediatorAction action,
        Session session,
        ICollection<Message> history,
        ICollection<MediatedRequest> openRequests,
        DateTimeOffset incomingCreatedAt)
    {
        switch (action)
        {
            case SendToParticipant send:
                AppendDeliveredMessage(
                    transcript,
                    session,
                    history,
                    send.ParticipantId,
                    send.Text,
                    incomingCreatedAt.AddSeconds(1));
                break;

            case SendToBoth send:
                AppendDeliveredMessage(
                    transcript,
                    session,
                    history,
                    session.ParticipantA.Id,
                    send.TextForParticipantA,
                    incomingCreatedAt.AddMilliseconds(500));
                AppendDeliveredMessage(
                    transcript,
                    session,
                    history,
                    session.ParticipantB.Id,
                    send.TextForParticipantB,
                    incomingCreatedAt.AddSeconds(1));
                break;

            case OpenMediatedRequest open:
                openRequests.Add(new MediatedRequest(
                    open.RequestId,
                    session.Id,
                    open.RequesterId,
                    open.RespondentId,
                    open.Summary,
                    MediatedRequestStatus.AwaitingResponse,
                    incomingCreatedAt));
                AppendDeliveredMessage(
                    transcript,
                    session,
                    history,
                    open.RespondentId,
                    open.TextForRespondent,
                    incomingCreatedAt.AddMilliseconds(500));
                AppendDeliveredMessage(
                    transcript,
                    session,
                    history,
                    open.RequesterId,
                    open.TextForRequester,
                    incomingCreatedAt.AddSeconds(1));
                break;

            case ResolveMediatedRequest resolve:
                RemoveOpenRequest(openRequests, resolve.RequestId);
                AppendDeliveredMessage(
                    transcript,
                    session,
                    history,
                    resolve.RequesterId,
                    resolve.TextForRequester,
                    incomingCreatedAt.AddMilliseconds(500));
                if (resolve.TextForRespondent is not null)
                {
                    AppendDeliveredMessage(
                        transcript,
                        session,
                        history,
                        resolve.RespondentId,
                        resolve.TextForRespondent,
                        incomingCreatedAt.AddSeconds(1));
                }
                break;

            case CancelMediatedRequest cancel:
                RemoveOpenRequest(openRequests, cancel.RequestId);
                AppendDeliveredMessage(
                    transcript,
                    session,
                    history,
                    cancel.RespondentId,
                    cancel.TextForRespondent,
                    incomingCreatedAt.AddMilliseconds(500));
                AppendDeliveredMessage(
                    transcript,
                    session,
                    history,
                    cancel.RequesterId,
                    cancel.TextForRequester,
                    incomingCreatedAt.AddSeconds(1));
                break;

            case NoAction:
                transcript.AppendLine("**Action:** `no_action`");
                transcript.AppendLine();
                break;

            default:
                throw new InvalidOperationException(
                    $"Unsupported mediator action '{action.GetType().Name}'.");
        }
    }

    private static void RemoveOpenRequest(
        ICollection<MediatedRequest> openRequests,
        Guid requestId)
    {
        var request = openRequests.SingleOrDefault(candidate => candidate.Id == requestId)
            ?? throw new InvalidDataException(
                $"Mediated request '{requestId}' is not open in this scenario.");
        openRequests.Remove(request);
    }

    private static void AppendDeliveredMessage(
        StringBuilder transcript,
        Session session,
        ICollection<Message> history,
        Guid recipientId,
        string text,
        DateTimeOffset createdAt)
    {
        var recipient = session.GetParticipant(recipientId);
        var role = recipient.Id == session.ParticipantA.Id ? "A" : "B";
        transcript.AppendLine($"**Mediator → Participant {role}:**");
        transcript.AppendLine();
        AppendQuote(transcript, text);
        transcript.AppendLine();
        history.Add(new Message(
            Guid.NewGuid(),
            session.Id,
            null,
            recipient.Id,
            MessageDirection.MediatorToParticipant,
            text,
            createdAt));
    }

    private static void AppendQuote(StringBuilder transcript, string text)
    {
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal)
                     .Replace('\r', '\n')
                     .Split('\n'))
        {
            transcript.AppendLine($"> {line}");
        }
    }
}
