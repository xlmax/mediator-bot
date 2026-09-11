using System.Text.Json;
using MediatorBot.Core;
using MediatorBot.Infrastructure;

namespace MediatorBot.Tests;

public sealed class OpenAiToolCallMapperTests
{
    private readonly Participant _participantA = new(Guid.NewGuid(), "A");
    private readonly Participant _participantB = new(Guid.NewGuid(), "B");
    private readonly OpenAiToolCallMapper _mapper = new();

    [Fact]
    public void Map_ConvertsClassifiedSendToParticipant()
    {
        var arguments = JsonSerializer.Serialize(new
        {
            participantId = _participantB.Id,
            text = "Сообщение для B",
            disclosureDecision = "MediatorDisclosure"
        });

        var action = _mapper.Map(
            CreateContext(),
            new OpenAiToolCall(OpenAiToolCatalog.SendToParticipant, arguments));

        var send = Assert.IsType<SendToParticipant>(action);
        Assert.Equal(_participantB.Id, send.ParticipantId);
        Assert.Equal("Сообщение для B", send.Text);
        Assert.Equal(
            DisclosureDecision.MediatorDisclosure,
            send.DisclosureDecision);
    }

    [Fact]
    public void Map_ConvertsPrivateResponseToCurrentAuthor()
    {
        var arguments = JsonSerializer.Serialize(new
        {
            participantId = _participantA.Id,
            text = "Ответ A",
            disclosureDecision = "PrivateResponse"
        });

        var action = _mapper.Map(
            CreateContext(),
            new OpenAiToolCall(OpenAiToolCatalog.SendToParticipant, arguments));

        var send = Assert.IsType<SendToParticipant>(action);
        Assert.Equal(DisclosureDecision.PrivateResponse, send.DisclosureDecision);
    }

    [Fact]
    public void Map_ConvertsSendToBothWithDisclosureDecision()
    {
        var action = _mapper.Map(
            CreateContext(),
            new OpenAiToolCall(
                OpenAiToolCatalog.SendToBoth,
                """
                {
                  "textForA":"Для A",
                  "textForB":"Для B",
                  "disclosureDecision":"ExplicitTransfer"
                }
                """));

        var send = Assert.IsType<SendToBoth>(action);
        Assert.Equal("Для A", send.TextForParticipantA);
        Assert.Equal("Для B", send.TextForParticipantB);
        Assert.Equal(DisclosureDecision.ExplicitTransfer, send.DisclosureDecision);
    }

    [Fact]
    public void Map_OpensMediatedRequestForOtherParticipant()
    {
        var arguments = JsonSerializer.Serialize(new
        {
            respondentId = _participantB.Id,
            summary = "Уточнить текущие занятия B",
            textForRequester = "Я уточню, но ответ зависит от согласия B.",
            textForRespondent = "A спрашивает, что ты делаешь. Можно не отвечать.",
            disclosureDecision = "ExplicitTransfer"
        });

        var action = _mapper.Map(
            CreateContext(),
            new OpenAiToolCall(OpenAiToolCatalog.OpenMediatedRequest, arguments));

        var open = Assert.IsType<OpenMediatedRequest>(action);
        Assert.NotEqual(Guid.Empty, open.RequestId);
        Assert.Equal(_participantA.Id, open.RequesterId);
        Assert.Equal(_participantB.Id, open.RespondentId);
        Assert.Equal(DisclosureDecision.ExplicitTransfer, open.DisclosureDecision);
    }

    [Theory]
    [InlineData("Declined", "MediatorDisclosure")]
    [InlineData("NoShareableAnswer", "MediatorDisclosure")]
    [InlineData("Answered", "ExplicitTransfer")]
    public void Map_ResolvesOpenMediatedRequest(
        string outcome,
        string disclosureDecision)
    {
        var session = CreateSession();
        var request = new MediatedRequest(
            Guid.NewGuid(),
            session.Id,
            _participantA.Id,
            _participantB.Id,
            "Уточнить текущие занятия B",
            MediatedRequestStatus.AwaitingResponse,
            DateTimeOffset.UtcNow);
        var context = CreateContext(session, _participantB, request);
        const string proposedText =
            "B отказался, сердится и не хочет объяснять причину.";
        var arguments = JsonSerializer.Serialize(new
        {
            requestId = request.Id,
            outcome,
            textForRequester = proposedText,
            disclosureDecision
        });

        var action = _mapper.Map(
            context,
            new OpenAiToolCall(OpenAiToolCatalog.ResolveMediatedRequest, arguments));

        var resolve = Assert.IsType<ResolveMediatedRequest>(action);
        Assert.Equal(request.Id, resolve.RequestId);
        Assert.Equal(_participantA.Id, resolve.RequesterId);
        Assert.Equal(_participantB.Id, resolve.RespondentId);
        Assert.Equal(
            Enum.Parse<MediatedRequestOutcome>(outcome),
            resolve.Outcome);
        Assert.Equal(
            Enum.Parse<DisclosureDecision>(disclosureDecision),
            resolve.DisclosureDecision);
        var answered = outcome == nameof(MediatedRequestOutcome.Answered);
        Assert.Equal(
            answered
                ? proposedText
                : OpenAiToolCallMapper.NeutralRequestClosureText,
            resolve.TextForRequester);
        Assert.Equal(
            answered
                ? null
                : OpenAiToolCallMapper.NeutralRespondentClosureText,
            resolve.TextForRespondent);
    }

    [Fact]
    public void Map_RejectsResolutionByRequesterInsteadOfRespondent()
    {
        var session = CreateSession();
        var request = new MediatedRequest(
            Guid.NewGuid(),
            session.Id,
            _participantA.Id,
            _participantB.Id,
            "Запрос",
            MediatedRequestStatus.AwaitingResponse,
            DateTimeOffset.UtcNow);
        var context = CreateContext(session, _participantA, request);
        var arguments = JsonSerializer.Serialize(new
        {
            requestId = request.Id,
            outcome = "Declined",
            textForRequester = "Запрос закрыт.",
            disclosureDecision = "MediatorDisclosure"
        });

        Assert.Throws<InvalidDataException>(() => _mapper.Map(
            context,
            new OpenAiToolCall(OpenAiToolCatalog.ResolveMediatedRequest, arguments)));
    }

    [Fact]
    public void Map_ConvertsNoAction()
    {
        var action = _mapper.Map(
            CreateContext(),
            new OpenAiToolCall(OpenAiToolCatalog.NoAction, "{}"));

        var noAction = Assert.IsType<NoAction>(action);
        Assert.Equal(DisclosureDecision.NoAction, noAction.DisclosureDecision);
    }

    [Fact]
    public void Map_RejectsParticipantOutsideCurrentSession()
    {
        var arguments = JsonSerializer.Serialize(new
        {
            participantId = Guid.NewGuid(),
            text = "Не должно быть доставлено",
            disclosureDecision = "MediatorDisclosure"
        });

        Assert.Throws<ArgumentException>(() => _mapper.Map(
            CreateContext(),
            new OpenAiToolCall(OpenAiToolCatalog.SendToParticipant, arguments)));
    }

    [Fact]
    public void Map_RejectsMissingDisclosureDecision()
    {
        var arguments = JsonSerializer.Serialize(new
        {
            participantId = _participantA.Id,
            text = "Ответ"
        });

        Assert.Throws<InvalidDataException>(() => _mapper.Map(
            CreateContext(),
            new OpenAiToolCall(OpenAiToolCatalog.SendToParticipant, arguments)));
    }

    [Fact]
    public void Map_RejectsPrivateResponseAddressedToOtherParticipant()
    {
        var arguments = JsonSerializer.Serialize(new
        {
            participantId = _participantB.Id,
            text = "Ответ",
            disclosureDecision = "PrivateResponse"
        });

        Assert.Throws<InvalidDataException>(() => _mapper.Map(
            CreateContext(),
            new OpenAiToolCall(OpenAiToolCatalog.SendToParticipant, arguments)));
    }

    [Fact]
    public void Map_RejectsExplicitTransferAddressedBackToAuthor()
    {
        var arguments = JsonSerializer.Serialize(new
        {
            participantId = _participantA.Id,
            text = "Ответ",
            disclosureDecision = "ExplicitTransfer"
        });

        Assert.Throws<InvalidDataException>(() => _mapper.Map(
            CreateContext(),
            new OpenAiToolCall(OpenAiToolCatalog.SendToParticipant, arguments)));
    }

    [Fact]
    public void Map_RejectsUnknownTool()
    {
        Assert.Throws<InvalidOperationException>(() => _mapper.Map(
            CreateContext(),
            new OpenAiToolCall("send_to_email", "{}")));
    }

    private Session CreateSession() =>
        new(Guid.NewGuid(), _participantA, _participantB);

    private ConversationContext CreateContext() =>
        CreateContext(CreateSession(), _participantA);

    private static ConversationContext CreateContext(
        Session session,
        Participant author,
        params MediatedRequest[] openRequests)
    {
        var incoming = new Message(
            Guid.NewGuid(),
            session.Id,
            author.Id,
            null,
            MessageDirection.ParticipantToMediator,
            "Текущее сообщение",
            DateTimeOffset.UtcNow);
        return new ConversationContext(session, author, [incoming], incoming)
        {
            OpenMediatedRequests = openRequests
        };
    }
}
