using MediatorBot.Core;
using MediatorBot.Infrastructure;

namespace MediatorBot.Tests;

public sealed class OpenAiConversationSummaryGeneratorTests
{
    [Fact]
    public async Task GenerateAsync_UsesDedicatedPrivacyAndAntiRuminationPrompt()
    {
        OpenAiChatRequest? captured = null;
        var client = new StubChatClient(request =>
        {
            captured = request;
            return ValidResponse();
        });
        var generator = CreateGenerator(client);
        var (session, messages) = CreateInput();

        var summary = await generator.GenerateAsync(session, null, messages, 6000);

        Assert.Equal("A context", summary.PrivateContextFromParticipantA);
        Assert.NotNull(captured);
        Assert.Single(captured.Tools);
        Assert.Equal("replace_conversation_memory", captured.Tools[0].Name);
        Assert.Contains("Не сохраняй разовые бытовые раздражения", captured.SystemPrompt);
        Assert.Contains("не означает согласие на передачу", captured.SystemPrompt);
        Assert.Contains("не доказывает паттерн", captured.SystemPrompt);
        Assert.Contains("Participant A сообщает", captured.SystemPrompt);
        Assert.Contains("обязательно остаётся в его приватной секции", captured.SystemPrompt);
        Assert.Contains("зарезервирована только для рисков безопасности", captured.SystemPrompt);
        Assert.Contains("СООБЩЕНИЯ, КОТОРЫЕ БУДУТ УДАЛЕНЫ", captured.ConversationPrompt);
        Assert.Contains(messages[0].Message.Text, captured.ConversationPrompt);
    }

    [Fact]
    public async Task GenerateAsync_IncludesPreviousMemoryAsReplacementInput()
    {
        OpenAiChatRequest? captured = null;
        var client = new StubChatClient(request =>
        {
            captured = request;
            return ValidResponse();
        });
        var generator = CreateGenerator(client);
        var (session, messages) = CreateInput();
        var previous = new ConversationSummaryContent(
            "old-a",
            "old-b",
            "old-shared",
            "old-safety");

        await generator.GenerateAsync(session, previous, messages, 6000);

        Assert.NotNull(captured);
        Assert.Contains("old-a", captured.ConversationPrompt);
        Assert.Contains("old-safety", captured.ConversationPrompt);
        Assert.Contains("полную замену", captured.ConversationPrompt);
    }

    [Fact]
    public async Task GenerateAsync_RejectsAdditionalToolArguments()
    {
        var client = new StubChatClient(_ => Response(
            """
            {
              "privateContextFromParticipantA":"",
              "privateContextFromParticipantB":"",
              "sharedContextAndAgreements":"",
              "boundariesAndSafety":"",
              "unexpected":"value"
            }
            """));
        var generator = CreateGenerator(client);
        var (session, messages) = CreateInput();

        var exception = await Assert.ThrowsAsync<OpenAiProtocolException>(() =>
            generator.GenerateAsync(session, null, messages, 6000));

        Assert.Equal(OpenAiProtocolFailureReason.InvalidToolCall, exception.Reason);
    }

    [Fact]
    public async Task GenerateAsync_RejectsAssistantTextAlongsideToolCall()
    {
        var client = new StubChatClient(_ => ValidResponse("unexpected text"));
        var generator = CreateGenerator(client);
        var (session, messages) = CreateInput();

        var exception = await Assert.ThrowsAsync<OpenAiProtocolException>(() =>
            generator.GenerateAsync(session, null, messages, 6000));

        Assert.Equal(OpenAiProtocolFailureReason.UnexpectedAssistantText, exception.Reason);
    }

    [Fact]
    public async Task GenerateAsync_RejectsSummaryAboveCombinedCharacterLimit()
    {
        var client = new StubChatClient(_ => Response(
            $$"""
            {
              "privateContextFromParticipantA":"{{new string('a', 6)}}",
              "privateContextFromParticipantB":"{{new string('b', 6)}}",
              "sharedContextAndAgreements":"",
              "boundariesAndSafety":""
            }
            """));
        var generator = CreateGenerator(client);
        var (session, messages) = CreateInput();

        var exception = await Assert.ThrowsAsync<OpenAiProtocolException>(() =>
            generator.GenerateAsync(session, null, messages, 10));

        Assert.Equal(OpenAiProtocolFailureReason.InvalidToolCall, exception.Reason);
    }

    private static OpenAiConversationSummaryGenerator CreateGenerator(
        IOpenAiChatClient client) => new(
        client,
        new ConversationCompactionOptions
        {
            MaxOutputTokens = 2000
        });

    private static (Session Session, IReadOnlyList<SequencedMessage> Messages) CreateInput()
    {
        var participantA = new Participant(Guid.NewGuid(), "Анна");
        var participantB = new Participant(Guid.NewGuid(), "Борис");
        var session = new Session(Guid.NewGuid(), participantA, participantB);
        var message = new Message(
            Guid.NewGuid(),
            session.Id,
            participantA.Id,
            null,
            MessageDirection.ParticipantToMediator,
            "Синтетическое приватное сообщение",
            DateTimeOffset.UtcNow);
        return (session, [new SequencedMessage(1, message)]);
    }

    private static OpenAiChatResponse ValidResponse(string? assistantText = null) => Response(
        """
        {
          "privateContextFromParticipantA":"A context",
          "privateContextFromParticipantB":"B context",
          "sharedContextAndAgreements":"shared",
          "boundariesAndSafety":"safety"
        }
        """,
        assistantText);

    private static OpenAiChatResponse Response(
        string arguments,
        string? assistantText = null) => new(
        [new OpenAiToolCall("replace_conversation_memory", arguments)],
        assistantText,
        "test-model",
        new OpenAiTokenUsage(100, 20, 10));

    private sealed class StubChatClient(
        Func<OpenAiChatRequest, OpenAiChatResponse> handler) : IOpenAiChatClient
    {
        public Task<OpenAiChatResponse> CompleteAsync(
            OpenAiChatRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(handler(request));
    }
}
