namespace MediatorBot.Core;

public sealed record Message
{
    public Message(
        Guid id,
        Guid sessionId,
        Guid? authorId,
        Guid? recipientId,
        MessageDirection direction,
        string text,
        DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        if (direction == MessageDirection.ParticipantToMediator &&
            (authorId is null || recipientId is not null))
        {
            throw new ArgumentException(
                "An incoming message requires an author and cannot have a recipient.");
        }

        if (direction == MessageDirection.MediatorToParticipant &&
            (authorId is not null || recipientId is null))
        {
            throw new ArgumentException(
                "An outgoing mediator message requires a recipient and cannot have a participant author.");
        }

        Id = id;
        SessionId = sessionId;
        AuthorId = authorId;
        RecipientId = recipientId;
        Direction = direction;
        Text = text;
        CreatedAt = createdAt;
    }

    public Guid Id { get; }

    public Guid SessionId { get; }

    public Guid? AuthorId { get; }

    public Guid? RecipientId { get; }

    public MessageDirection Direction { get; }

    public string Text { get; }

    public DateTimeOffset CreatedAt { get; }
}
