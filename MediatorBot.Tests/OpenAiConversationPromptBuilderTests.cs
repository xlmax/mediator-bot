using MediatorBot.Core;
using MediatorBot.Infrastructure;

namespace MediatorBot.Tests;

public sealed class OpenAiConversationPromptBuilderTests
{
    [Fact]
    public void Build_IdentifiesBothParticipantsAndRecipientsInSharedHistory()
    {
        var participantA = new Participant(Guid.NewGuid(), "Алекс");
        var participantB = new Participant(Guid.NewGuid(), "Борис");
        var session = new Session(Guid.NewGuid(), participantA, participantB);
        var start = DateTimeOffset.UtcNow;
        var fromB = new Message(
            Guid.NewGuid(),
            session.Id,
            participantB.Id,
            null,
            MessageDirection.ParticipantToMediator,
            "Мне кажется, он закрылся",
            start);
        var toB = new Message(
            Guid.NewGuid(),
            session.Id,
            null,
            participantB.Id,
            MessageDirection.MediatorToParticipant,
            "Дистанция воспринимается болезненно",
            start.AddSeconds(1));
        var current = new Message(
            Guid.NewGuid(),
            session.Id,
            participantA.Id,
            null,
            MessageDirection.ParticipantToMediator,
            "Меня задел разговор",
            start.AddSeconds(2));
        var context = new ConversationContext(
            session,
            participantA,
            [fromB, toB, current],
            current);

        var prompt = new OpenAiConversationPromptBuilder().Build(context);

        Assert.Contains($"Participant A: ParticipantId={participantA.Id:D}", prompt);
        Assert.Contains("DisplayName=\"Алекс\"", prompt);
        Assert.Contains($"Participant B: ParticipantId={participantB.Id:D}", prompt);
        Assert.Contains("DisplayName=\"Борис\"", prompt);
        Assert.Contains("[Participant B]", prompt);
        Assert.Contains("[Mediator -> Participant B]", prompt);
        Assert.Contains("Дистанция воспринимается болезненно", prompt);
        Assert.Contains("Текущее входящее сообщение:", prompt);
        Assert.Contains($"[Participant A | ParticipantId={participantA.Id:D}]", prompt);
        Assert.Equal(1, CountOccurrences(prompt, "Меня задел разговор"));
    }

    [Fact]
    public void Build_IncludesDurableOpenMediatedRequests()
    {
        var participantA = new Participant(Guid.NewGuid(), "Алекс");
        var participantB = new Participant(Guid.NewGuid(), "Борис");
        var session = new Session(Guid.NewGuid(), participantA, participantB);
        var incoming = new Message(
            Guid.NewGuid(),
            session.Id,
            participantB.Id,
            null,
            MessageDirection.ParticipantToMediator,
            "Ответ на вопрос",
            DateTimeOffset.UtcNow);
        var request = new MediatedRequest(
            Guid.NewGuid(),
            session.Id,
            participantA.Id,
            participantB.Id,
            "Уточнить готовность B к разговору",
            MediatedRequestStatus.AwaitingResponse,
            DateTimeOffset.UtcNow.AddMinutes(-1));
        var context = new ConversationContext(
            session,
            participantB,
            [incoming],
            incoming)
        {
            OpenMediatedRequests = [request]
        };

        var prompt = new OpenAiConversationPromptBuilder().Build(context);

        Assert.Contains("OPEN MEDIATED REQUESTS", prompt);
        Assert.Contains($"RequestId={request.Id:D}", prompt);
        Assert.Contains("Requester=Participant A", prompt);
        Assert.Contains("Respondent=Participant B", prompt);
        Assert.Contains(request.Summary, prompt);
    }

    [Fact]
    public void SystemPrompt_SeparatesMediatorKnowledgeFromDisclosure()
    {
        var prompt = OpenAiConversationPromptBuilder.SystemPrompt;

        Assert.Contains("знать всё не означает рассказывать всё", prompt);
        Assert.Contains("Mediator knowledge", prompt);
        Assert.Contains("Participant-visible information", prompt);
        Assert.Contains("каждое входящее сообщение — Private", prompt);
        Assert.Contains("не дают разрешения раскрывать", prompt);
        Assert.Contains("только «да/нет» не создают разрешения", prompt);
        Assert.Contains("Не выдавай подробный отчёт", prompt);
        Assert.Contains("минимальное раскрытие", prompt);
        Assert.Contains("утверждения участников, а не установленные факты", prompt);
        Assert.Contains("не должно постоянно синхронизировать", prompt);
        Assert.Contains("MEDIATED REQUEST LIFECYCLE", prompt);
        Assert.Contains("Не оставляй инициатора в ожидании", prompt);
    }

    [Fact]
    public void SystemPrompt_DefinesProportionalAntiRuminationPolicy()
    {
        var prompt = OpenAiConversationPromptBuilder.SystemPrompt;

        Assert.Contains("PROPORTIONALITY AND ANTI-RUMINATION", prompt);
        Assert.Contains("Не каждое раздражение требует анализа", prompt);
        Assert.Contains("Не создавай глубину там, где её может не быть", prompt);
        Assert.Contains("VENT, REFLECT, MEDIATE", prompt);
        Assert.Contains("Предпочитай естественное угасание мелких эмоций", prompt);
        Assert.Contains("Не превращай себя в журнал претензий", prompt);
        Assert.Contains("Не поддерживай grievance amplification", prompt);
        Assert.Contains("сохраняется ли проблема после снижения эмоций", prompt);
        Assert.Contains("Анти-руминационная политика не означает", prompt);
        Assert.Contains("не задавай обязательный уточняющий вопрос", prompt);
        Assert.Contains("не ослабляет INFORMATION BOUNDARY", prompt);
        Assert.Contains("не даёт разрешения раскрывать информацию", prompt);
        Assert.True(
            prompt.IndexOf("PROPORTIONALITY AND ANTI-RUMINATION", StringComparison.Ordinal) <
            prompt.IndexOf("DISCLOSURE DECISIONS", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("PrivateResponse")]
    [InlineData("MediatorDisclosure")]
    [InlineData("ExplicitTransfer")]
    [InlineData("SafetyDisclosure")]
    [InlineData("NoAction")]
    public void SystemPrompt_DefinesDisclosureDecision(string decision)
    {
        Assert.Contains(decision, OpenAiConversationPromptBuilder.SystemPrompt);
    }

    [Fact]
    public void SystemPrompt_RequiresClassifiedToolBasedMediation()
    {
        var prompt = OpenAiConversationPromptBuilder.SystemPrompt;

        Assert.Contains("только через предоставленные инструменты", prompt);
        Assert.Contains("укажи соответствующий disclosureDecision", prompt);
        Assert.Contains("PrivateResponse допустим только", prompt);
        Assert.Contains("используй DisplayName", prompt);
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(search, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += search.Length;
        }

        return count;
    }
}
