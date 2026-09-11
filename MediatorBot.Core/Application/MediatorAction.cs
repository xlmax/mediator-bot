namespace MediatorBot.Core;

public enum DisclosureDecision
{
    PrivateResponse,
    MediatorDisclosure,
    ExplicitTransfer,
    SafetyDisclosure,
    NoAction
}

public abstract record MediatorAction(DisclosureDecision DisclosureDecision);

public sealed record SendToParticipant(
    Guid ParticipantId,
    string Text,
    DisclosureDecision DisclosureDecision)
    : MediatorAction(DisclosureDecision);

public sealed record SendToBoth(
    string TextForParticipantA,
    string TextForParticipantB,
    DisclosureDecision DisclosureDecision)
    : MediatorAction(DisclosureDecision);

public enum MediatedRequestOutcome
{
    Answered,
    Declined,
    NoShareableAnswer
}

public sealed record OpenMediatedRequest(
    Guid RequestId,
    Guid RequesterId,
    Guid RespondentId,
    string Summary,
    string TextForRequester,
    string TextForRespondent,
    DisclosureDecision DisclosureDecision)
    : MediatorAction(DisclosureDecision);

public sealed record ResolveMediatedRequest(
    Guid RequestId,
    Guid RequesterId,
    Guid RespondentId,
    MediatedRequestOutcome Outcome,
    string TextForRequester,
    string? TextForRespondent,
    DisclosureDecision DisclosureDecision)
    : MediatorAction(DisclosureDecision);

public sealed record CancelMediatedRequest(
    Guid RequestId,
    Guid RequesterId,
    Guid RespondentId,
    string TextForRequester,
    string TextForRespondent,
    DisclosureDecision DisclosureDecision)
    : MediatorAction(DisclosureDecision);

public sealed record NoAction() : MediatorAction(DisclosureDecision.NoAction);
