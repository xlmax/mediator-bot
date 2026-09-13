namespace MediatorBot.Core;

public sealed record PendingExternalTurn(
    Guid Id,
    Guid SessionId,
    Guid ParticipantId,
    string Source,
    string ExternalUpdateId,
    long SourceSequence,
    string ExternalUserId,
    string Text,
    DateTimeOffset CreatedAt,
    int AttemptCount = 0);
