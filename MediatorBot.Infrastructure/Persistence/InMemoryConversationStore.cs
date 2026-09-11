using MediatorBot.Core;

namespace MediatorBot.Infrastructure;

public sealed class InMemoryConversationStore :
    IConversationStore,
    IParticipantIdentityStore,
    IExternalUpdateStore,
    IMediatedRequestStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, Session> _sessions = [];
    private readonly Dictionary<Guid, List<Message>> _messagesBySession = [];
    private readonly Dictionary<
        (string Provider, string ExternalId),
        (Guid SessionId, Guid ParticipantId)> _identityBindings = [];
    private readonly HashSet<
        (string Source, Guid SessionId, string ExternalUpdateId)> _externalUpdates = [];
    private readonly Dictionary<Guid, MediatedRequest> _mediatedRequests = [];

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

    public Task CreateAsync(
        MediatedRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_lock)
        {
            if (!_sessions.TryGetValue(request.SessionId, out var session))
            {
                throw new KeyNotFoundException(
                    $"Session '{request.SessionId}' was not found.");
            }

            session.GetParticipant(request.RequesterId);
            session.GetParticipant(request.RespondentId);
            if (request.RequesterId == request.RespondentId ||
                request.Status != MediatedRequestStatus.PendingDelivery ||
                request.ResolvedAt is not null ||
                string.IsNullOrWhiteSpace(request.Summary))
            {
                throw new ArgumentException(
                    "A new mediated request must be pending delivery and connect two participants.",
                    nameof(request));
            }

            if (!_mediatedRequests.TryAdd(request.Id, request))
            {
                throw new InvalidOperationException(
                    $"Mediated request '{request.Id}' already exists.");
            }
        }

        return Task.CompletedTask;
    }

    public Task MarkAwaitingResponseAsync(
        Guid sessionId,
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_lock)
        {
            var request = GetRequest(sessionId, requestId);
            if (request.Status != MediatedRequestStatus.PendingDelivery)
            {
                throw new InvalidOperationException(
                    $"Mediated request '{requestId}' is not pending delivery.");
            }

            _mediatedRequests[requestId] = request with
            {
                Status = MediatedRequestStatus.AwaitingResponse
            };
        }

        return Task.CompletedTask;
    }

    public Task ResolveAsync(
        Guid sessionId,
        Guid requestId,
        MediatedRequestStatus status,
        DateTimeOffset resolvedAt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateFinalStatus(status);

        lock (_lock)
        {
            var request = GetRequest(sessionId, requestId);
            if (request.Status != MediatedRequestStatus.AwaitingResponse)
            {
                throw new InvalidOperationException(
                    $"Mediated request '{requestId}' is not awaiting a response.");
            }

            _mediatedRequests[requestId] = request with
            {
                Status = status,
                ResolvedAt = resolvedAt
            };
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MediatedRequest>> GetOpenAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_lock)
        {
            if (!_sessions.ContainsKey(sessionId))
            {
                throw new KeyNotFoundException($"Session '{sessionId}' was not found.");
            }

            return Task.FromResult<IReadOnlyList<MediatedRequest>>(
                _mediatedRequests.Values
                    .Where(request =>
                        request.SessionId == sessionId &&
                        request.Status == MediatedRequestStatus.AwaitingResponse)
                    .OrderBy(request => request.CreatedAt)
                    .ToArray());
        }
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

    private MediatedRequest GetRequest(Guid sessionId, Guid requestId)
    {
        if (!_mediatedRequests.TryGetValue(requestId, out var request) ||
            request.SessionId != sessionId)
        {
            throw new KeyNotFoundException(
                $"Mediated request '{requestId}' was not found in session '{sessionId}'.");
        }

        return request;
    }

    private static void ValidateFinalStatus(MediatedRequestStatus status)
    {
        if (status is not (
            MediatedRequestStatus.Answered or
            MediatedRequestStatus.Declined or
            MediatedRequestStatus.NoShareableAnswer or
            MediatedRequestStatus.Cancelled))
        {
            throw new ArgumentOutOfRangeException(
                nameof(status),
                status,
                "A mediated request must resolve to a final status.");
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
