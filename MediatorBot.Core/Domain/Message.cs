namespace MediatorBot.Core;

public sealed record Message(
    Guid Id,
    Guid SessionId,
    Guid AuthorId,
    string Text,
    DateTimeOffset Timestamp);
