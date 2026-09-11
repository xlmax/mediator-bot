using MediatorBot.Core;
using MediatorBot.Infrastructure;
using Microsoft.Extensions.Logging;

namespace MediatorBot.Tests;

public sealed class OpenAiModelRuntimeTests
{
    [Fact]
    public async Task ProcessAsync_SendsAllToolsAndMapsToolCalls()
    {
        OpenAiChatRequest? capturedRequest = null;
        var client = new StubChatClient(request =>
        {
            capturedRequest = request;
            return Response(
                [new OpenAiToolCall(OpenAiToolCatalog.NoAction, "{}")]);
        });
        var runtime = CreateRuntime(client);

        var result = await runtime.ProcessAsync(CreateContext("Текущий текст"));

        Assert.IsType<NoAction>(Assert.Single(result.Actions));
        Assert.NotNull(capturedRequest);
        Assert.Equal(1500, capturedRequest.MaxOutputTokens);
        Assert.Equal(
            [
                OpenAiToolCatalog.SendToParticipant,
                OpenAiToolCatalog.SendToBoth,
                OpenAiToolCatalog.OpenMediatedRequest,
                OpenAiToolCatalog.ResolveMediatedRequest,
                OpenAiToolCatalog.CancelMediatedRequest,
                OpenAiToolCatalog.NoAction
            ],
            capturedRequest.Tools.Select(tool => tool.Name));
        Assert.Contains("Текущий текст", capturedRequest.ConversationPrompt);
        Assert.All(
            capturedRequest.Tools.Where(tool => tool.Name != OpenAiToolCatalog.NoAction),
            tool => Assert.Contains("\"disclosureDecision\"", tool.ParametersJson));
    }

    [Fact]
    public async Task ProcessAsync_TreatsUnexpectedAssistantTextAsProtocolFailure()
    {
        const string rawAssistantText = "Обычный текст модели";
        var logger = new RecordingLogger<OpenAiModelRuntime>();
        var context = CreateContext("Входящее сообщение");
        var client = new StubChatClient(_ => new OpenAiChatResponse(
            [],
            rawAssistantText,
            "test-model",
            new OpenAiTokenUsage(10, 0, 5)));
        var runtime = CreateRuntime(client, logger);

        var exception = await Assert.ThrowsAsync<OpenAiProtocolException>(() =>
            runtime.ProcessAsync(context));

        Assert.Equal(
            OpenAiProtocolFailureReason.UnexpectedAssistantText,
            exception.Reason);
        var log = Assert.Single(logger.Messages);
        Assert.Contains(nameof(OpenAiProtocolException), log);
        Assert.DoesNotContain(rawAssistantText, log);
        Assert.DoesNotContain(context.IncomingMessage.Text, log);
    }

    [Fact]
    public async Task ProcessAsync_RejectsMultipleToolCalls()
    {
        var client = new StubChatClient(_ => Response(
            [
                new OpenAiToolCall(OpenAiToolCatalog.NoAction, "{}"),
                new OpenAiToolCall(OpenAiToolCatalog.NoAction, "{}")
            ]));
        var runtime = CreateRuntime(client);

        var exception = await Assert.ThrowsAsync<OpenAiProtocolException>(() =>
            runtime.ProcessAsync(CreateContext("Входящее сообщение")));

        Assert.Equal(
            OpenAiProtocolFailureReason.MultipleToolCalls,
            exception.Reason);
    }

    [Fact]
    public async Task ProcessAsync_RejectsSendWithoutDisclosureDecision()
    {
        var context = CreateContext("Входящее сообщение");
        var client = new StubChatClient(_ => Response(
            [
                new OpenAiToolCall(
                    OpenAiToolCatalog.SendToParticipant,
                    $$"""
                    {
                      "participantId":"{{context.Author.Id:D}}",
                      "text":"Ответ"
                    }
                    """)
            ]));
        var runtime = CreateRuntime(client);

        var exception = await Assert.ThrowsAsync<OpenAiProtocolException>(() =>
            runtime.ProcessAsync(context));

        Assert.Equal(OpenAiProtocolFailureReason.InvalidToolCall, exception.Reason);
    }

    [Fact]
    public async Task ProcessAsync_LogsUsageWithoutPrivateText()
    {
        const string privateText = "секретное содержимое разговора";
        var logger = new RecordingLogger<OpenAiModelRuntime>();
        var client = new StubChatClient(_ => new OpenAiChatResponse(
            [new OpenAiToolCall(OpenAiToolCatalog.NoAction, "{}")],
            null,
            "test-model",
            new OpenAiTokenUsage(123, 45, 17)));
        var runtime = CreateRuntime(client, logger);

        await runtime.ProcessAsync(CreateContext(privateText));

        var log = Assert.Single(logger.Messages);
        Assert.Contains("Model=test-model", log);
        Assert.Contains("InputTokens=123", log);
        Assert.Contains("CachedInputTokens=45", log);
        Assert.Contains("OutputTokens=17", log);
        Assert.Contains("DisclosureDecisions=NoAction", log);
        Assert.DoesNotContain(privateText, log);
    }

    private static OpenAiModelRuntime CreateRuntime(
        IOpenAiChatClient client,
        ILogger<OpenAiModelRuntime>? logger = null) => new(
            client,
            new OpenAiConversationPromptBuilder(),
            new OpenAiToolCallMapper(),
            new OpenAiModelRuntimeOptions
            {
                ApiKey = "not-used-by-test-client",
                Model = "test-model",
                MaxOutputTokens = 1500
            },
            logger);

    private static OpenAiChatResponse Response(
        IReadOnlyList<OpenAiToolCall> toolCalls) => new(
            toolCalls,
            null,
            "test-model",
            new OpenAiTokenUsage(100, 20, 10));

    private static ConversationContext CreateContext(string text)
    {
        var participantA = new Participant(Guid.NewGuid(), "A");
        var participantB = new Participant(Guid.NewGuid(), "B");
        var session = new Session(Guid.NewGuid(), participantA, participantB);
        var incoming = new Message(
            Guid.NewGuid(),
            session.Id,
            participantA.Id,
            null,
            MessageDirection.ParticipantToMediator,
            text,
            DateTimeOffset.UtcNow);
        return new ConversationContext(session, participantA, [incoming], incoming);
    }

    private sealed class StubChatClient(
        Func<OpenAiChatRequest, OpenAiChatResponse> handler) : IOpenAiChatClient
    {
        public Task<OpenAiChatResponse> CompleteAsync(
            OpenAiChatRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(handler(request));
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
