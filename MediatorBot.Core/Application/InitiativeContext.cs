namespace MediatorBot.Core;

public sealed record InitiativeContext(
    Session Session,
    IReadOnlyList<Message> History,
    Message LatestParticipantMessage,
    DateTimeOffset Now,
    IReadOnlyList<InitiativeDecision> RecentDecisions,
    IReadOnlyList<InitiativeParticipantPreference> ParticipantPreferences,
    IReadOnlyDictionary<Guid, int> DeliveredContactsLast24Hours)
{
    public ConversationSummary? Summary { get; init; }

    public IReadOnlyList<MediatedRequest> OpenMediatedRequests { get; init; } = [];

    public Participant ParticipantA => Session.ParticipantA;

    public Participant ParticipantB => Session.ParticipantB;
}
