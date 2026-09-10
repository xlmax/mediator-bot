namespace MediatorBot.Core;

public sealed record ConversationContext(
    Session Session,
    Participant Author,
    IReadOnlyList<Message> History,
    Message IncomingMessage)
{
    public Participant ParticipantA => Session.ParticipantA;

    public Participant ParticipantB => Session.ParticipantB;
}
