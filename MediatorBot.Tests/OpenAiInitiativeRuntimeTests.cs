using MediatorBot.Core;
using MediatorBot.Infrastructure;

namespace MediatorBot.Tests;

public sealed class OpenAiInitiativeRuntimeTests
{
    [Fact]
    public async Task EvaluateAsync_UsesDedicatedToolAndMapsDecision()
    {
        OpenAiChatRequest? captured = null;
        var context = CreateContext();
        var client = new StubChatClient(request =>
        {
            captured = request;
            return new OpenAiChatResponse(
                [new OpenAiToolCall(
                    OpenAiInitiativeToolCatalog.RecordDecision,
                    $$"""
                    {
                      "phase":"RepairWindow",
                      "confidence":"Medium",
                      "decisionKind":"ContactParticipant",
                      "targetParticipantId":"{{context.ParticipantA.Id:D}}",
                      "intent":"AssessReadiness",
                      "reasonCode":"RepairOpportunity",
                      "operationalRationale":"Появилось безопасное окно для короткой проверки.",
                      "textForParticipantA":"Готов ли ты спокойно вернуться к теме?",
                      "textForParticipantB":null,
                      "reevaluateAfterMinutes":120,
                      "pauseParticipantId":null,
                      "pauseForMinutes":null
                    }
                    """)],
                null,
                "test-model",
                new OpenAiTokenUsage(100, 20, 30));
        });
        var runtime = CreateRuntime(client);

        var proposal = await runtime.EvaluateAsync(context);

        Assert.Equal(InitiativeDecisionKind.ContactParticipant, proposal.DecisionKind);
        Assert.Equal(context.ParticipantA.Id, proposal.TargetParticipantId);
        Assert.Equal(InitiativeIntent.AssessReadiness, proposal.Intent);
        Assert.NotNull(captured);
        Assert.Single(captured.Tools);
        Assert.Equal(OpenAiInitiativeToolCatalog.RecordDecision, captured.Tools[0].Name);
        Assert.Contains(context.LatestParticipantMessage.CreatedAt.ToString("O"),
            captured.ConversationPrompt);
        Assert.Contains("никто не написал прямо сейчас", captured.SystemPrompt);
    }

    [Fact]
    public async Task EvaluateAsync_RejectsUnknownParticipant()
    {
        var client = new StubChatClient(_ => new OpenAiChatResponse(
            [new OpenAiToolCall(
                OpenAiInitiativeToolCatalog.RecordDecision,
                $$"""
                {
                  "phase":"Calm",
                  "confidence":"Low",
                  "decisionKind":"ContactParticipant",
                  "targetParticipantId":"{{Guid.NewGuid():D}}",
                  "intent":"CheckIn",
                  "reasonCode":"Other",
                  "operationalRationale":"Проверка.",
                  "textForParticipantA":"Текст",
                  "textForParticipantB":null,
                  "reevaluateAfterMinutes":60,
                  "pauseParticipantId":null,
                  "pauseForMinutes":null
                }
                """)],
            null,
            "test-model",
            new OpenAiTokenUsage(100, 0, 20)));

        await Assert.ThrowsAsync<OpenAiProtocolException>(() =>
            CreateRuntime(client).EvaluateAsync(CreateContext()));
    }

    private static OpenAiInitiativeRuntime CreateRuntime(IOpenAiChatClient client)
    {
        var initiativeOptions = new InitiativeOptions();
        return new OpenAiInitiativeRuntime(
            client,
            new OpenAiInitiativePromptBuilder(initiativeOptions),
            new OpenAiModelRuntimeOptions
            {
                ApiKey = "unused",
                Model = "test-model",
                MaxOutputTokens = 1000
            },
            initiativeOptions);
    }

    private static InitiativeContext CreateContext()
    {
        var participantA = new Participant(Guid.NewGuid(), "A");
        var participantB = new Participant(Guid.NewGuid(), "B");
        var session = new Session(Guid.NewGuid(), participantA, participantB);
        var message = new Message(
            Guid.NewGuid(),
            session.Id,
            participantA.Id,
            null,
            MessageDirection.ParticipantToMediator,
            "Хочу позже спокойно поговорить",
            new DateTimeOffset(2026, 9, 24, 10, 0, 0, TimeSpan.Zero));
        return new InitiativeContext(
            session,
            [message],
            message,
            message.CreatedAt.AddHours(2),
            [],
            [
                new(session.Id, participantA.Id, true, null, session.CreatedAt),
                new(session.Id, participantB.Id, true, null, session.CreatedAt)
            ],
            new Dictionary<Guid, int>
            {
                [participantA.Id] = 0,
                [participantB.Id] = 0
            });
    }

    private sealed class StubChatClient(
        Func<OpenAiChatRequest, OpenAiChatResponse> handler) : IOpenAiChatClient
    {
        public Task<OpenAiChatResponse> CompleteAsync(
            OpenAiChatRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(handler(request));
    }
}
