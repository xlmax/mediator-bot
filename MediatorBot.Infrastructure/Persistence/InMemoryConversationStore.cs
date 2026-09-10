using MediatorBot.Core;

namespace MediatorBot.Infrastructure;

public sealed class InMemoryConversationStore : IConversationStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, Session> _sessions = [];
    private readonly Dictionary<Guid, List<Message>> _messagesBySession = [];

    public InMemoryConversationStore(IEnumerable<Session>? sessions = null)
    {
        foreach (var session in sessions ?? [])
        {
            AddSession(session);
        }
    }

    public void AddSession(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);

        lock (_lock)
        {
            if (!_sessions.TryAdd(session.Id, session))
            {
                throw new InvalidOperationException($"Session '{session.Id}' already exists.");
            }

            _messagesBySession.Add(session.Id, []);
        }
    }

    public Task CreateSessionAsync(
        Session session,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AddSession(session);
        return Task.CompletedTask;
    }

    public Task<Session?> GetSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_lock)
        {
            return Task.FromResult(_sessions.GetValueOrDefault(sessionId));
        }
    }

    public Task SaveMessageAsync(
        Message message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_lock)
        {
            if (!_sessions.TryGetValue(message.SessionId, out var session))
            {
                throw new KeyNotFoundException($"Session '{message.SessionId}' was not found.");
            }

            ValidateParticipants(session, message);
            _messagesBySession[message.SessionId].Add(message);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Message>> GetHistoryAsync(
        Guid sessionId,
        int? maxMessages = null,
        CancellationToken cancellationToken = default)
    {
        if (maxMessages is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxMessages));
        }

        cancellationToken.ThrowIfCancellationRequested();

        lock (_lock)
        {
            if (!_messagesBySession.TryGetValue(sessionId, out var messages))
            {
                throw new KeyNotFoundException($"Session '{sessionId}' was not found.");
            }

            IEnumerable<Message> history = messages.OrderBy(message => message.CreatedAt);
            if (maxMessages is not null)
            {
                history = history.TakeLast(maxMessages.Value);
            }

            return Task.FromResult<IReadOnlyList<Message>>(history.ToArray());
        }
    }

    private static void ValidateParticipants(Session session, Message message)
    {
        if (message.AuthorId is Guid authorId)
        {
            session.GetParticipant(authorId);
        }

        if (message.RecipientId is Guid recipientId)
        {
            session.GetParticipant(recipientId);
        }
    }
}
