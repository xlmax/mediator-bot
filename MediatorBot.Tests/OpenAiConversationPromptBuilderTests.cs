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
    public void SystemPrompt_RequiresWarmConcreteMovement()
    {
        var prompt = OpenAiConversationPromptBuilder.SystemPrompt;

        Assert.Contains("тёплый и деятельный медиатор", prompt);
        Assert.Contains("одновременно понятым и чуть ближе", prompt);
        Assert.Contains("Теплота не равна автоматическому согласию", prompt);
        Assert.Contains("одного сочувственного комментария недостаточно", prompt);
        Assert.Contains("предложи следующий шаг сам", prompt);
        Assert.Contains("не требуется подтверждённая позиция партнёра", prompt);
        Assert.Contains("ограниченную посредническую процедуру", prompt);
        Assert.Contains("если тот же мост или предложение уже были ему доставлены", prompt);
        Assert.Contains("Не используй осторожность раскрытия как повод ничего не предложить", prompt);
    }

    [Fact]
    public void SystemPrompt_TreatsPrivacyAsSilentConditionalBoundary()
    {
        var prompt = OpenAiConversationPromptBuilder.SystemPrompt;

        Assert.Contains("PRIVACY — SILENT BOUNDARY", prompt);
        Assert.Contains("mediator knowledge", prompt);
        Assert.Contains("raw private content", prompt);
        Assert.Contains("relationship-relevant meaning", prompt);
        Assert.Contains("внутренняя рабочая граница, а не тема каждого ответа", prompt);
        Assert.Contains("не обещай «ничего не передавать»", prompt);
        Assert.Contains("Озвучивай границу кратко только когда", prompt);
        Assert.Contains("только «да/нет» не дают разрешения", prompt);
        Assert.Contains("не становятся safe paraphrase", prompt);
        Assert.Contains("формально объявляет границу и тут же раскрывает содержание", prompt);
        Assert.Contains("После необходимого ограничения возвращай разговор", prompt);
        Assert.Contains("не раскрывай факт обращения", prompt);
    }

    [Fact]
    public void SystemPrompt_BalancesRuminationMediationAndSafety()
    {
        var prompt = OpenAiConversationPromptBuilder.SystemPrompt;

        Assert.Contains("PROPORTIONALITY AND RUMINATION", prompt);
        Assert.Contains("короткий живой отклик", prompt);
        Assert.Contains("Не усиливай переживание собственными словами", prompt);
        Assert.Contains("почувствовал себя брошенным", prompt);
        Assert.Contains("что готов сделать сам", prompt);
        Assert.Contains("Признание эмоции не означает подтверждения обвинительной версии", prompt);
        Assert.Contains("архивом доказательств", prompt);
        Assert.Contains("Не предлагай таблицу, перечень или систематизацию", prompt);
        Assert.Contains("Не отвечай приглашениями «выговорись»", prompt);
        Assert.Contains("сформулировать действие, взять паузу или закончить тему", prompt);
        Assert.Contains("не являются «обычной руминацией»", prompt);
        Assert.Contains("BridgeIntervention", prompt);
        Assert.Contains("MEDIATED REQUESTS", prompt);
        Assert.Contains("NoShareableAnswer", prompt);
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
    public void SystemPrompt_RequiresNaturalToolBasedMediation()
    {
        var prompt = OpenAiConversationPromptBuilder.SystemPrompt;

        Assert.Contains("естественным русским языком", prompt);
        Assert.Contains("без канцелярита", prompt);
        Assert.Contains("не обязано быть только эмоциональной поддержкой", prompt);
        Assert.Contains("Просьба «помоги сказать»", prompt);
        Assert.Contains("только из оскорбления", prompt);
        Assert.Contains("ровно одно действие через предоставленные инструменты", prompt);
        Assert.Contains("не добавляй обычный assistant text", prompt);
        Assert.Contains("Используй DisplayName естественно", prompt);
    }

    [Fact]
    public void SystemPrompt_IsNotDominatedByProhibitions()
    {
        var prompt = OpenAiConversationPromptBuilder.SystemPrompt;

        Assert.True(prompt.Length < 12_000);
        Assert.True(CountOccurrences(prompt.ToLowerInvariant(), "не ") < 70);
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
