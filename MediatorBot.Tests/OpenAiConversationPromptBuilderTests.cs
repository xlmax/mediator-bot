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
        Assert.Contains($"Participant B: ParticipantId={participantB.Id:D}", prompt);
        Assert.Contains("[Participant B]", prompt);
        Assert.Contains("[Mediator -> Participant B]", prompt);
        Assert.Contains("Дистанция воспринимается болезненно", prompt);
        Assert.Contains("Текущее входящее сообщение:", prompt);
        Assert.Contains($"[Participant A | ParticipantId={participantA.Id:D}]", prompt);
        Assert.Equal(1, CountOccurrences(prompt, "Меня задел разговор"));
    }

    [Fact]
    public void SystemPrompt_RequiresPrivateToolBasedMediation()
    {
        Assert.Contains("приватные сообщения", OpenAiConversationPromptBuilder.SystemPrompt);
        Assert.Contains("только через предоставленные инструменты", OpenAiConversationPromptBuilder.SystemPrompt);
        Assert.Contains("не видит приватные сообщения другого", OpenAiConversationPromptBuilder.SystemPrompt);
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
