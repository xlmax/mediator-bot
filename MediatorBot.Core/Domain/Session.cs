namespace MediatorBot.Core;

public sealed record Session
{
    public Session(
        Guid id,
        Participant participantA,
        Participant participantB,
        DateTimeOffset? createdAt = null)
    {
        if (participantA.Id == participantB.Id)
        {
            throw new ArgumentException("A session requires two different participants.");
        }

        Id = id;
        ParticipantA = participantA;
        ParticipantB = participantB;
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow;
    }

    public Guid Id { get; }

    public Participant ParticipantA { get; }

    public Participant ParticipantB { get; }

    public DateTimeOffset CreatedAt { get; }

    public Participant GetParticipant(Guid participantId) => participantId switch
    {
        _ when participantId == ParticipantA.Id => ParticipantA,
        _ when participantId == ParticipantB.Id => ParticipantB,
        _ => throw new ArgumentException(
            $"Participant '{participantId}' does not belong to session '{Id}'.",
            nameof(participantId))
    };
}
