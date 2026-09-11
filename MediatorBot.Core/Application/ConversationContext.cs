namespace MediatorBot.Core;

public sealed record ConversationContext(
    Session Session,
    Participant Author,
    IReadOnlyList<Message> History,
    Message IncomingMessage)
{
    public ConversationSummary? Summary { get; init; }

    public IReadOnlyList<MediatedRequest> OpenMediatedRequests { get; init; } = [];

    public Participant ParticipantA => Session.ParticipantA;

    public Participant ParticipantB => Session.ParticipantB;
}
