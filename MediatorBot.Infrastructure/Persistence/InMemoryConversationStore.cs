using MediatorBot.Core;

namespace MediatorBot.Infrastructure;

public sealed class InMemoryConversationStore :
    IConversationStore,
    IParticipantIdentityStore,
    IExternalUpdateStore,
    IExternalTurnQueueStore,
    ITurnExecutionStore,
    IMediatedRequestStore,
    IConversationCompactionStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, Session> _sessions = [];
    private readonly Dictionary<Guid, List<SequencedMessage>> _messagesBySession = [];
    private readonly Dictionary<
        (string Provider, string ExternalId),
        (Guid SessionId, Guid ParticipantId)> _identityBindings = [];
    private readonly HashSet<
        (string Source, Guid SessionId, string ExternalUpdateId)> _externalUpdates = [];
    private readonly Dictionary<Guid, MediatedRequest> _mediatedRequests = [];
    private readonly Dictionary<Guid, PendingExternalTurn> _pendingTurns = [];
    private readonly Dictionary<Guid, string> _modelResultsByTurn = [];
    private readonly Dictionary<Guid, TurnDelivery> _turnDeliveries = [];
    private readonly HashSet<Guid> _recordedIncomingTurns = [];
    private readonly Dictionary<Guid, ConversationSummary> _conversationSummaries = [];
    private long _nextMessageSequence;

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
            var existing = _messagesBySession.Values
                .SelectMany(messages => messages)
                .FirstOrDefault(entry => entry.Message.Id == message.Id)
                ?.Message;
            if (existing is not null)
            {
                EnsureSameMessage(existing, message);
                return Task.CompletedTask;
            }

            if (_recordedIncomingTurns.Contains(message.Id))
            {
                EnsureMatchesPendingTurn(message);
                return Task.CompletedTask;
            }

            _messagesBySession[message.SessionId].Add(
                new SequencedMessage(++_nextMessageSequence, message));
            if (message.Direction == MessageDirection.ParticipantToMediator &&
                _pendingTurns.ContainsKey(message.Id))
            {
                EnsureMatchesPendingTurn(message);
                _recordedIncomingTurns.Add(message.Id);
            }
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

            if (_mediatedRequests.TryGetValue(request.Id, out var existing))
            {
                EnsureSameRequestIdentity(existing, request);
                return Task.CompletedTask;
            }

            _mediatedRequests.Add(request.Id, request);
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
            if (request.Status == MediatedRequestStatus.AwaitingResponse)
            {
                return Task.CompletedTask;
            }

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
            if (request.Status == status && request.ResolvedAt is not null)
            {
                return Task.CompletedTask;
            }

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

            IEnumerable<Message> history = messages
                .OrderBy(entry => entry.Sequence)
                .Select(entry => entry.Message);
            if (maxMessages is not null)
            {
                history = history.TakeLast(maxMessages.Value);
            }

            return Task.FromResult<IReadOnlyList<Message>>(history.ToArray());
        }
    }

    public Task<bool> TryEnqueueAsync(
        PendingExternalTurn turn,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentException.ThrowIfNullOrWhiteSpace(turn.Source);
        ArgumentException.ThrowIfNullOrWhiteSpace(turn.ExternalUpdateId);
        ArgumentException.ThrowIfNullOrWhiteSpace(turn.ExternalUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(turn.Text);
        ArgumentOutOfRangeException.ThrowIfNegative(turn.SourceSequence);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_lock)
        {
            var session = _sessions.GetValueOrDefault(turn.SessionId)
                ?? throw new KeyNotFoundException(
                    $"Session '{turn.SessionId}' was not found.");
            session.GetParticipant(turn.ParticipantId);
            if (!_externalUpdates.Add(
                    (turn.Source, turn.SessionId, turn.ExternalUpdateId)))
            {
                return Task.FromResult(false);
            }

            if (!_pendingTurns.TryAdd(turn.Id, turn))
            {
                _externalUpdates.Remove(
                    (turn.Source, turn.SessionId, turn.ExternalUpdateId));
                throw new InvalidOperationException(
                    $"Pending turn '{turn.Id}' already exists.");
            }

            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<PendingExternalTurn>> GetPendingAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            EnsureSessionExists(sessionId);
            return Task.FromResult<IReadOnlyList<PendingExternalTurn>>(
                _pendingTurns.Values
                    .Where(turn => turn.SessionId == sessionId)
                    .OrderBy(turn => turn.SourceSequence)
                    .ThenBy(turn => turn.CreatedAt)
                    .ThenBy(turn => turn.Id)
                    .ToArray());
        }
    }

    public Task CompleteAsync(
        Guid sessionId,
        Guid turnId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            EnsureSessionExists(sessionId);
            if (!_pendingTurns.TryGetValue(turnId, out var turn) ||
                turn.SessionId != sessionId)
            {
                throw new KeyNotFoundException(
                    $"Pending turn '{turnId}' was not found in session '{sessionId}'.");
            }

            _pendingTurns.Remove(turnId);
            _modelResultsByTurn.Remove(turnId);
            _recordedIncomingTurns.Remove(turnId);
            foreach (var deliveryId in _turnDeliveries.Values
                         .Where(delivery => delivery.TurnId == turnId)
                         .Select(delivery => delivery.Id)
                         .ToArray())
            {
                _turnDeliveries.Remove(deliveryId);
            }
        }

        return Task.CompletedTask;
    }

    public Task<string?> GetModelResultAsync(
        Guid sessionId,
        Guid turnId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            GetPendingTurn(sessionId, turnId);
            return Task.FromResult(_modelResultsByTurn.GetValueOrDefault(turnId));
        }
    }

    public Task SaveModelResultAsync(
        Guid sessionId,
        Guid turnId,
        string modelResultJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelResultJson);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            GetPendingTurn(sessionId, turnId);
            if (_modelResultsByTurn.TryGetValue(turnId, out var existing))
            {
                if (!string.Equals(existing, modelResultJson, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Turn '{turnId}' already has a different persisted model result.");
                }

                return Task.CompletedTask;
            }

            _modelResultsByTurn.Add(turnId, modelResultJson);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TurnDelivery>> EnsureDeliveryPlanAsync(
        TurnDeliveryPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ValidateDeliveryPlan(plan);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            GetPendingTurn(plan.SessionId, plan.TurnId);
            var session = _sessions[plan.SessionId];
            session.GetParticipant(plan.ParticipantId);
            var existing = _turnDeliveries.Values
                .Where(delivery =>
                    delivery.TurnId == plan.TurnId &&
                    delivery.DeliveryKey == plan.DeliveryKey)
                .OrderBy(delivery => delivery.ChunkIndex)
                .ToArray();
            if (existing.Length > 0)
            {
                EnsureSameDeliveryPlan(existing, plan);
                return Task.FromResult<IReadOnlyList<TurnDelivery>>(existing);
            }

            var logicalMessageId = Guid.NewGuid();
            var deliveries = plan.Chunks
                .Select((text, index) => new TurnDelivery(
                    Guid.NewGuid(),
                    plan.TurnId,
                    plan.SessionId,
                    plan.ParticipantId,
                    logicalMessageId,
                    plan.DeliveryKey,
                    index + 1,
                    plan.Chunks.Count,
                    text,
                    plan.ActionType,
                    plan.DisclosureDecision,
                    TurnDeliveryStatus.Pending,
                    plan.CreatedAt))
                .ToArray();
            foreach (var delivery in deliveries)
            {
                _turnDeliveries.Add(delivery.Id, delivery);
            }

            return Task.FromResult<IReadOnlyList<TurnDelivery>>(deliveries);
        }
    }

    public Task MarkDeliveryAttemptingAsync(
        Guid sessionId,
        Guid turnId,
        Guid deliveryId,
        DateTimeOffset attemptedAt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            var delivery = GetTurnDelivery(sessionId, turnId, deliveryId);
            if (delivery.Status != TurnDeliveryStatus.Delivered)
            {
                _turnDeliveries[deliveryId] = delivery with
                {
                    Status = TurnDeliveryStatus.Attempting,
                    AttemptedAt = attemptedAt
                };
            }
        }

        return Task.CompletedTask;
    }

    public Task RecordDeliveryAsync(
        Guid sessionId,
        Guid turnId,
        Guid deliveryId,
        DateTimeOffset deliveredAt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            var delivery = GetTurnDelivery(sessionId, turnId, deliveryId);
            if (delivery.Status == TurnDeliveryStatus.Delivered)
            {
                return Task.CompletedTask;
            }

            if (delivery.Status != TurnDeliveryStatus.Attempting)
            {
                throw new InvalidOperationException(
                    $"Turn delivery '{deliveryId}' was not marked as attempting.");
            }

            delivery = delivery with
            {
                Status = TurnDeliveryStatus.Delivered,
                DeliveredAt = deliveredAt
            };
            _turnDeliveries[deliveryId] = delivery;
            var deliveredChunks = _turnDeliveries.Values
                .Where(candidate =>
                    candidate.LogicalMessageId == delivery.LogicalMessageId &&
                    candidate.Status == TurnDeliveryStatus.Delivered)
                .OrderBy(candidate => candidate.ChunkIndex)
                .ToArray();
            var text = string.Concat(deliveredChunks.Select(candidate => candidate.Text));
            var createdAt = deliveredChunks.Min(candidate => candidate.DeliveredAt)!.Value;
            var message = new Message(
                delivery.LogicalMessageId,
                sessionId,
                null,
                delivery.ParticipantId,
                MessageDirection.MediatorToParticipant,
                text,
                createdAt);
            var messages = _messagesBySession[sessionId];
            var existingIndex = messages.FindIndex(entry =>
                entry.Message.Id == delivery.LogicalMessageId);
            if (existingIndex < 0)
            {
                messages.Add(new SequencedMessage(++_nextMessageSequence, message));
            }
            else
            {
                messages[existingIndex] = new SequencedMessage(
                    messages[existingIndex].Sequence,
                    message);
            }
        }

        return Task.CompletedTask;
    }

    public Task<ConversationSummary?> GetSummaryAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            EnsureSessionExists(sessionId);
            return Task.FromResult(_conversationSummaries.GetValueOrDefault(sessionId));
        }
    }

    public Task<ConversationCompactionBatch?> GetCompactionBatchAsync(
        Guid sessionId,
        int triggerMessageCount,
        int triggerCharacterCount,
        int retainRecentMessageCount,
        int retainRecentCharacterCount,
        CancellationToken cancellationToken = default)
    {
        ValidateCompactionThresholds(
            triggerMessageCount,
            triggerCharacterCount,
            retainRecentMessageCount,
            retainRecentCharacterCount);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_lock)
        {
            EnsureSessionExists(sessionId);
            var messages = _messagesBySession[sessionId]
                .OrderBy(entry => entry.Sequence)
                .ToArray();
            var firstPendingIndex = Array.FindIndex(messages, entry =>
                _recordedIncomingTurns.Contains(entry.Message.Id) &&
                _pendingTurns.ContainsKey(entry.Message.Id));
            if (firstPendingIndex >= 0)
            {
                messages = messages.Take(firstPendingIndex).ToArray();
            }

            if (messages.Length < triggerMessageCount &&
                messages.Sum(entry => entry.Message.Text.Length) < triggerCharacterCount)
            {
                return Task.FromResult<ConversationCompactionBatch?>(null);
            }

            var retainedCount = 0;
            var retainedCharacters = 0;
            for (var index = messages.Length - 1; index >= 0; index--)
            {
                var messageCharacters = messages[index].Message.Text.Length;
                if (retainedCount >= retainRecentMessageCount ||
                    (retainedCount > 0 &&
                     retainedCharacters + messageCharacters > retainRecentCharacterCount))
                {
                    break;
                }

                retainedCount++;
                retainedCharacters += messageCharacters;
            }

            var compactedCount = messages.Length - retainedCount;
            if (compactedCount <= 0)
            {
                return Task.FromResult<ConversationCompactionBatch?>(null);
            }

            var compactedMessages = messages.Take(compactedCount).ToArray();
            var previousSummary = _conversationSummaries.GetValueOrDefault(sessionId);
            return Task.FromResult<ConversationCompactionBatch?>(new(
                sessionId,
                previousSummary?.Version ?? 0,
                compactedMessages[^1].Sequence,
                previousSummary?.Content,
                compactedMessages));
        }
    }

    public Task CommitCompactionAsync(
        Guid sessionId,
        long expectedSummaryVersion,
        long compactedThroughSequence,
        ConversationSummaryContent summary,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedSummaryVersion);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(compactedThroughSequence);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_lock)
        {
            EnsureSessionExists(sessionId);
            var currentSummary = _conversationSummaries.GetValueOrDefault(sessionId);
            if ((currentSummary?.Version ?? 0) != expectedSummaryVersion)
            {
                throw new InvalidOperationException(
                    "Conversation summary changed while compaction was running.");
            }

            var messages = _messagesBySession[sessionId];
            var compactedMessageCount = messages.Count(entry =>
                entry.Sequence <= compactedThroughSequence);
            if (compactedMessageCount == 0)
            {
                throw new InvalidOperationException(
                    "Compaction cutoff does not contain any conversation messages.");
            }

            _conversationSummaries[sessionId] = new ConversationSummary(
                sessionId,
                expectedSummaryVersion + 1,
                compactedThroughSequence,
                summary,
                updatedAt);
            messages.RemoveAll(entry => entry.Sequence <= compactedThroughSequence);
        }

        return Task.CompletedTask;
    }

    private void EnsureSessionExists(Guid sessionId)
    {
        if (!_sessions.ContainsKey(sessionId))
        {
            throw new KeyNotFoundException($"Session '{sessionId}' was not found.");
        }
    }

    private static void ValidateCompactionThresholds(
        int triggerMessageCount,
        int triggerCharacterCount,
        int retainRecentMessageCount,
        int retainRecentCharacterCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(triggerMessageCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(triggerCharacterCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(retainRecentMessageCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(retainRecentCharacterCount);
        if (retainRecentMessageCount >= triggerMessageCount ||
            retainRecentCharacterCount >= triggerCharacterCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retainRecentMessageCount),
                "Retained history must be below its compaction trigger.");
        }
    }

    private PendingExternalTurn GetPendingTurn(Guid sessionId, Guid turnId)
    {
        if (!_pendingTurns.TryGetValue(turnId, out var turn) ||
            turn.SessionId != sessionId)
        {
            throw new KeyNotFoundException(
                $"Pending turn '{turnId}' was not found in session '{sessionId}'.");
        }

        return turn;
    }

    private TurnDelivery GetTurnDelivery(
        Guid sessionId,
        Guid turnId,
        Guid deliveryId)
    {
        GetPendingTurn(sessionId, turnId);
        if (!_turnDeliveries.TryGetValue(deliveryId, out var delivery) ||
            delivery.SessionId != sessionId ||
            delivery.TurnId != turnId)
        {
            throw new KeyNotFoundException(
                $"Turn delivery '{deliveryId}' was not found.");
        }

        return delivery;
    }

    private void EnsureMatchesPendingTurn(Message message)
    {
        if (!_pendingTurns.TryGetValue(message.Id, out var turn) ||
            turn.SessionId != message.SessionId ||
            turn.ParticipantId != message.AuthorId ||
            message.RecipientId is not null ||
            message.Direction != MessageDirection.ParticipantToMediator ||
            !string.Equals(turn.Text, message.Text, StringComparison.Ordinal) ||
            turn.CreatedAt != message.CreatedAt)
        {
            throw new InvalidOperationException(
                $"Message '{message.Id}' conflicts with its pending turn.");
        }
    }

    private static void EnsureSameMessage(Message existing, Message candidate)
    {
        if (existing.SessionId != candidate.SessionId ||
            existing.AuthorId != candidate.AuthorId ||
            existing.RecipientId != candidate.RecipientId ||
            existing.Direction != candidate.Direction ||
            !string.Equals(existing.Text, candidate.Text, StringComparison.Ordinal) ||
            existing.CreatedAt != candidate.CreatedAt)
        {
            throw new InvalidOperationException(
                $"Message '{candidate.Id}' already exists with different content.");
        }
    }

    private static void ValidateDeliveryPlan(TurnDeliveryPlan plan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.DeliveryKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.ActionType);
        if (plan.Chunks.Count == 0 ||
            plan.Chunks.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "A delivery plan must contain non-empty chunks.",
                nameof(plan));
        }
    }

    private static void EnsureSameDeliveryPlan(
        IReadOnlyList<TurnDelivery> existing,
        TurnDeliveryPlan plan)
    {
        if (existing.Count != plan.Chunks.Count ||
            existing.Any(delivery =>
                delivery.SessionId != plan.SessionId ||
                delivery.ParticipantId != plan.ParticipantId ||
                delivery.ChunkCount != plan.Chunks.Count ||
                delivery.ActionType != plan.ActionType ||
                delivery.DisclosureDecision != plan.DisclosureDecision ||
                !string.Equals(
                    delivery.Text,
                    plan.Chunks[delivery.ChunkIndex - 1],
                    StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"Delivery plan '{plan.DeliveryKey}' conflicts with persisted deliveries.");
        }
    }

    private static void EnsureSameRequestIdentity(
        MediatedRequest existing,
        MediatedRequest candidate)
    {
        if (existing.Id != candidate.Id ||
            existing.SessionId != candidate.SessionId ||
            existing.RequesterId != candidate.RequesterId ||
            existing.RespondentId != candidate.RespondentId ||
            !string.Equals(existing.Summary, candidate.Summary, StringComparison.Ordinal) ||
            existing.CreatedAt != candidate.CreatedAt)
        {
            throw new InvalidOperationException(
                $"Mediated request '{candidate.Id}' already exists with different data.");
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
