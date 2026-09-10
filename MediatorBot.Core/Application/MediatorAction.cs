namespace MediatorBot.Core;

public abstract record MediatorAction;

public sealed record SendToParticipant(
    Guid ParticipantId,
    string Text) : MediatorAction;

public sealed record SendToBoth(
    string TextForParticipantA,
    string TextForParticipantB) : MediatorAction;

public sealed record NoAction : MediatorAction;
