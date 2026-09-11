using MediatorBot.Core;

namespace MediatorBot.Infrastructure;

public sealed class InMemoryConversationStore :
    IConversationStore,
    IParticipantIdentityStore,
    IExternalUpdateStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, Session> _sessions = [];
    private readonly Dictionary<Guid, List<Message>> _messagesBySession = [];
    private readonly Dictionary<
        (string Provider, string ExternalId),
        (Guid SessionId, Guid ParticipantId)> _identityBindings = [];
    private readonly HashSet<
        (string Source, Guid SessionId, string ExternalUpdateId)> _externalUpdates = [];

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

    public Task UpdateParticipantDisplayNamesAsync(
        Guid sessionId,
        IReadOnlyDictionary<Guid, string> displayNames,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(displayNames);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_lock)
        {
            if (!_sessions.TryGetValue(sessionId, out var session))
            {
                throw new KeyNotFoundException($"Session '{sessionId}' was not found.");
            }

            ValidateDisplayNames(session, displayNames);
            _sessions[sessionId] = new Session(
                session.Id,
                new Participant(
                    session.ParticipantA.Id,
                    displayNames[session.ParticipantA.Id]),
                new Participant(
                    session.ParticipantB.Id,
                    displayNames[session.ParticipantB.Id]),
                session.CreatedAt);
        }

        return Task.CompletedTask;
    }

    public Task<bool> TryRegisterAsync(
        string source,
        Guid sessionId,
        string externalUpdateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalUpdateId);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_lock)
        {
            if (!_sessions.ContainsKey(sessionId))
            {
                throw new KeyNotFoundException($"Session '{sessionId}' was not found.");
            }

            return Task.FromResult(
                _externalUpdates.Add((source, sessionId, externalUpdateId)));
        }
    }

    public Task EnsureBindingsAsync(
        Guid sessionId,
        string identityProvider,
        IReadOnlyCollection<ParticipantIdentityBinding> expectedBindings,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityProvider);
        ArgumentNullException.ThrowIfNull(expectedBindings);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_lock)
        {
            if (!_sessions.TryGetValue(sessionId, out var session))
            {
                throw new KeyNotFoundException($"Session '{sessionId}' was not found.");
            }

            ValidateExpectedBindings(session, expectedBindings);
            var existingBindings = _identityBindings
                .Where(binding =>
                    binding.Key.Provider == identityProvider &&
                    binding.Value.SessionId == sessionId)
                .ToArray();

            if (existingBindings.Length > 0)
            {
                var matches = existingBindings.Length == expectedBindings.Count &&
                    expectedBindings.All(expected =>
                        existingBindings.Any(existing =>
                            existing.Key.ExternalId == expected.ExternalId &&
                            existing.Value.ParticipantId == expected.ParticipantId));
                if (!matches)
                {
                    throw new InvalidOperationException(
                        $"Identity bindings for session '{sessionId}' do not match the configured participants.");
                }

                return Task.CompletedTask;
            }

            foreach (var expected in expectedBindings)
            {
                if (_identityBindings.ContainsKey((identityProvider, expected.ExternalId)))
                {
                    throw new InvalidOperationException(
                        "An external identity is already bound to another participant.");
                }
            }

            foreach (var expected in expectedBindings)
            {
                _identityBindings.Add(
                    (identityProvider, expected.ExternalId),
                    (sessionId, expected.ParticipantId));
            }
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

    private static void ValidateDisplayNames(
        Session session,
        IReadOnlyDictionary<Guid, string> displayNames)
    {
        if (displayNames.Count != 2 ||
            !displayNames.ContainsKey(session.ParticipantA.Id) ||
            !displayNames.ContainsKey(session.ParticipantB.Id))
        {
            throw new ArgumentException(
                "Display names must be supplied for both session participants.",
                nameof(displayNames));
        }

        foreach (var displayName in displayNames.Values)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        }
    }

    private static void ValidateExpectedBindings(
        Session session,
        IReadOnlyCollection<ParticipantIdentityBinding> expectedBindings)
    {
        if (expectedBindings.Count == 0 ||
            expectedBindings.Select(binding => binding.ParticipantId).Distinct().Count() !=
                expectedBindings.Count ||
            expectedBindings.Select(binding => binding.ExternalId).Distinct().Count() !=
                expectedBindings.Count)
        {
            throw new ArgumentException(
                "Participant identity bindings must be non-empty and unique.",
                nameof(expectedBindings));
        }

        foreach (var binding in expectedBindings)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(binding.ExternalId);
            session.GetParticipant(binding.ParticipantId);
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
