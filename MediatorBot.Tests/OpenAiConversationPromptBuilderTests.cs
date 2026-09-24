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
    public void Build_IncludesDurableMemoryWithExplicitPrivacyBoundary()
    {
        var participantA = new Participant(Guid.NewGuid(), "Алекс");
        var participantB = new Participant(Guid.NewGuid(), "Борис");
        var session = new Session(Guid.NewGuid(), participantA, participantB);
        var incoming = new Message(
            Guid.NewGuid(),
            session.Id,
            participantA.Id,
            null,
            MessageDirection.ParticipantToMediator,
            "Текущее",
            DateTimeOffset.UtcNow);
        var context = new ConversationContext(
            session,
            participantA,
            [incoming],
            incoming)
        {
            Summary = new ConversationSummary(
                session.Id,
                2,
                50,
                new ConversationSummaryContent(
                    "Приватный контекст A",
                    "Приватный контекст B",
                    "Общая договорённость",
                    "Важная граница"),
                DateTimeOffset.UtcNow)
        };

        var prompt = new OpenAiConversationPromptBuilder().Build(context);

        Assert.Contains("DURABLE MEDIATOR MEMORY", prompt);
        Assert.Contains("сама по себе не даёт разрешения", prompt);
        Assert.Contains("Приватный контекст A", prompt);
        Assert.Contains("Приватный контекст B", prompt);
        Assert.Contains("Общая договорённость", prompt);
        Assert.Contains("Важная граница", prompt);
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

        Assert.Contains("mediation-first with privacy constraints", prompt);
        Assert.Contains("не пересказывай лишнее, но активно передавай полезный смысл", prompt);
        Assert.Contains("Mediator knowledge", prompt);
        Assert.Contains("Participant-visible information", prompt);
        Assert.Contains("raw private content", prompt);
        Assert.Contains("relationship-relevant meaning", prompt);
        Assert.Contains("исходные формулировки каждого входящего сообщения — Private", prompt);
        Assert.Contains("не даёт неограниченного разрешения", prompt);
        Assert.Contains("не называй источник", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("только «да/нет» не создают разрешения", prompt);
        Assert.Contains("Не выдавай подробный отчёт", prompt);
        Assert.Contains("утверждения участников, а не установленные факты", prompt);
        Assert.Contains("не должна постоянно синхронизировать", prompt);
        Assert.Contains("MEDIATED REQUEST LIFECYCLE", prompt);
        Assert.Contains("Не оставляй инициатора в ожидании", prompt);
    }

    [Fact]
    public void SystemPrompt_RequiresMediationFirstBridgeSearch()
    {
        var prompt = OpenAiConversationPromptBuilder.SystemPrompt;

        Assert.Contains("MEDIATION-FIRST AND BRIDGE SEARCH", prompt);
        Assert.Contains("При каждом значимом turn активно ищи", prompt);
        Assert.Contains("взаимно совместимые желания", prompt);
        Assert.Contains("ошибочное представление одного участника о мотивах другого", prompt);
        Assert.Contains("Если найден мост, рассмотри инициативное сообщение", prompt);
        Assert.Contains("mediator inference", prompt);
        Assert.Contains("relationship-level meaning", prompt);
        Assert.Contains("отношения по-прежнему важны", prompt);
        Assert.Contains("не раскрывай сопутствующую летучую эмоцию", prompt);
        Assert.Contains("не упоминай гнев, сильное напряжение", prompt);
        Assert.Contains("редкие высокоценные смысловые единицы", prompt);
        Assert.Contains("Порог BridgeIntervention обычно требует", prompt);
        Assert.Contains("Односторонняя жалоба", prompt);
        Assert.Contains("Не сообщай второй стороне о предполагаемом заблуждении", prompt);
        Assert.Contains("Перед NoAction или ответом только текущему автору", prompt);
        Assert.Contains("Если да, предпочти mediated intervention", prompt);
        Assert.Contains("Инициативное сообщение другому участнику — нормальный инструмент", prompt);
        Assert.Contains("не путай активность с немедленной отправкой", prompt);
        Assert.Contains("не автоматического контакта с предполагаемым источником опасности", prompt);
        Assert.Contains("Не предупреждай, не увещевай и не конфронтируй", prompt);
        Assert.DoesNotContain(
            "При сомнении предпочитай PrivateResponse или NoAction",
            prompt);
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
        Assert.Contains("не отменяет INFORMATION BOUNDARY", prompt);
        Assert.Contains("значимый мост требует отдельной активной оценки", prompt);
        Assert.True(
            prompt.IndexOf("PROPORTIONALITY AND ANTI-RUMINATION", StringComparison.Ordinal) <
            prompt.IndexOf("DISCLOSURE DECISIONS", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("PrivateSupport")]
    [InlineData("SafeParaphrase")]
    [InlineData("BridgeIntervention")]
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
        Assert.Contains(
            "PrivateResponse соответствует PrivateSupport и допустим только",
            prompt);
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
