namespace MediatorBot.Core;

public sealed record ConversationSummaryContent(
    string PrivateContextFromParticipantA,
    string PrivateContextFromParticipantB,
    string SharedContextAndAgreements,
    string BoundariesAndSafety)
{
    public int CharacterCount =>
        PrivateContextFromParticipantA.Length +
        PrivateContextFromParticipantB.Length +
        SharedContextAndAgreements.Length +
        BoundariesAndSafety.Length;
}

public sealed record ConversationSummary(
    Guid SessionId,
    long Version,
    long CompactedThroughSequence,
    ConversationSummaryContent Content,
    DateTimeOffset UpdatedAt);

public sealed record SequencedMessage(long Sequence, Message Message);

public sealed record ConversationCompactionBatch(
    Guid SessionId,
    long ExpectedSummaryVersion,
    long CompactedThroughSequence,
    ConversationSummaryContent? PreviousSummary,
    IReadOnlyList<SequencedMessage> Messages);

public sealed record ConversationCompactionPlan(
    Session Session,
    ConversationCompactionBatch Batch);

public sealed record ConversationCompactionResult(
    long CompactedMessageCount,
    long CompactedThroughSequence,
    int SummaryCharacterCount);
