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
    public void Map_ConvertsSendToParticipant()
    {
        var session = CreateSession();
        var arguments = JsonSerializer.Serialize(new
        {
            participantId = _participantB.Id,
            text = "Сообщение для B"
        });

        var action = _mapper.Map(
            session,
            new OpenAiToolCall(OpenAiToolCatalog.SendToParticipant, arguments));

        var send = Assert.IsType<SendToParticipant>(action);
        Assert.Equal(_participantB.Id, send.ParticipantId);
        Assert.Equal("Сообщение для B", send.Text);
    }

    [Fact]
    public void Map_ConvertsSendToBothWithDifferentTexts()
    {
        var action = _mapper.Map(
            CreateSession(),
            new OpenAiToolCall(
                OpenAiToolCatalog.SendToBoth,
                """
                {"textForA":"Для A","textForB":"Для B"}
                """));

        var send = Assert.IsType<SendToBoth>(action);
        Assert.Equal("Для A", send.TextForParticipantA);
        Assert.Equal("Для B", send.TextForParticipantB);
    }

    [Fact]
    public void Map_ConvertsNoAction()
    {
        var action = _mapper.Map(
            CreateSession(),
            new OpenAiToolCall(OpenAiToolCatalog.NoAction, "{}"));

        Assert.IsType<NoAction>(action);
    }

    [Fact]
    public void Map_RejectsParticipantOutsideCurrentSession()
    {
        var outsiderId = Guid.NewGuid();
        var arguments = JsonSerializer.Serialize(new
        {
            participantId = outsiderId,
            text = "Не должно быть доставлено"
        });

        Assert.Throws<ArgumentException>(() => _mapper.Map(
            CreateSession(),
            new OpenAiToolCall(OpenAiToolCatalog.SendToParticipant, arguments)));
    }

    [Fact]
    public void Map_RejectsUnknownTool()
    {
        Assert.Throws<InvalidOperationException>(() => _mapper.Map(
            CreateSession(),
            new OpenAiToolCall("send_to_email", "{}")));
    }

    private Session CreateSession() =>
        new(Guid.NewGuid(), _participantA, _participantB);
}
