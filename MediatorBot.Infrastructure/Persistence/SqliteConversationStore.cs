using System.Globalization;
using System.Reflection;
using Dapper;
using MediatorBot.Core;
using Microsoft.Data.Sqlite;

namespace MediatorBot.Infrastructure;

public sealed class SqliteConversationStore :
    IConversationStore,
    IParticipantIdentityStore,
    IExternalUpdateStore,
    IExternalTurnQueueStore,
    IMediatedRequestStore,
    IConversationCompactionStore
{
    private const string SchemaResourceName =
        "MediatorBot.Infrastructure.Persistence.Schema.sql";

    private static readonly Lazy<bool> SqliteRuntime = new(() =>
    {
        SQLitePCL.Batteries_V2.Init();
        return true;
    });

    private readonly string _connectionString;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private bool _initialized;

    public SqliteConversationStore(SqliteConversationStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DatabaseKey);

        _ = SqliteRuntime.Value;

        var databasePath = Path.GetFullPath(options.DatabasePath);
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Password = options.DatabaseKey,
            Pooling = true
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
    }

    public async Task CreateSessionAsync(
        Session session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO Sessions (Id, CreatedAt)
            VALUES (@Id, @CreatedAt);
            """,
            new
            {
                Id = Format(session.Id),
                CreatedAt = Format(session.CreatedAt)
            },
            transaction,
            cancellationToken: cancellationToken));

        const string participantSql =
            """
            INSERT INTO Participants (Id, SessionId, DisplayName, ParticipantOrder)
            VALUES (@Id, @SessionId, @DisplayName, @ParticipantOrder);
            """;

        await connection.ExecuteAsync(new CommandDefinition(
            participantSql,
            ToParticipantParameters(session.Id, session.ParticipantA, 0),
            transaction,
            cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            participantSql,
            ToParticipantParameters(session.Id, session.ParticipantB, 1),
            transaction,
            cancellationToken: cancellationToken));

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<Session?> GetSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        using var result = await connection.QueryMultipleAsync(new CommandDefinition(
            """
            SELECT Id, CreatedAt
            FROM Sessions
            WHERE Id = @SessionId;

            SELECT Id, DisplayName, ParticipantOrder
            FROM Participants
            WHERE SessionId = @SessionId
            ORDER BY ParticipantOrder;
            """,
            new { SessionId = Format(sessionId) },
            cancellationToken: cancellationToken));

        var sessionRow = await result.ReadSingleOrDefaultAsync<SessionRow>();
        if (sessionRow is null)
        {
            return null;
        }

        var participantRows = (await result.ReadAsync<ParticipantRow>()).ToArray();
        if (participantRows.Length != 2 ||
            participantRows[0].ParticipantOrder != 0 ||
            participantRows[1].ParticipantOrder != 1)
        {
            throw new InvalidDataException(
                $"Session '{sessionId}' must contain exactly two ordered participants.");
        }

        return new Session(
            Guid.Parse(sessionRow.Id),
            ToParticipant(participantRows[0]),
            ToParticipant(participantRows[1]),
            ParseTimestamp(sessionRow.CreatedAt));
    }

    public async Task SaveMessageAsync(
        Message message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO Messages (
                Id, SessionId, AuthorId, RecipientId, Direction, Text, CreatedAt)
            VALUES (
                @Id, @SessionId, @AuthorId, @RecipientId, @Direction, @Text, @CreatedAt);
            """,
            new
            {
                Id = Format(message.Id),
                SessionId = Format(message.SessionId),
                AuthorId = Format(message.AuthorId),
                RecipientId = Format(message.RecipientId),
                Direction = message.Direction.ToString(),
                message.Text,
                CreatedAt = Format(message.CreatedAt)
            },
            cancellationToken: cancellationToken));
    }

    public async Task UpdateParticipantDisplayNamesAsync(
        Guid sessionId,
        IReadOnlyDictionary<Guid, string> displayNames,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(displayNames);
        ValidateDisplayNameValues(displayNames);
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var participantIds = (await connection.QueryAsync<string>(new CommandDefinition(
            """
            SELECT Id
            FROM Participants
            WHERE SessionId = @SessionId;
            """,
            new { SessionId = Format(sessionId) },
            transaction,
            cancellationToken: cancellationToken))).ToArray();

        if (participantIds.Length == 0)
        {
            throw new KeyNotFoundException($"Session '{sessionId}' was not found.");
        }

        if (participantIds.Length != displayNames.Count ||
            displayNames.Keys.Any(participantId =>
                !participantIds.Contains(Format(participantId), StringComparer.Ordinal)))
        {
            throw new ArgumentException(
                "Display names must be supplied for every session participant.",
                nameof(displayNames));
        }

        foreach (var displayName in displayNames)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE Participants
                SET DisplayName = @DisplayName
                WHERE SessionId = @SessionId AND Id = @ParticipantId;
                """,
                new
                {
                    SessionId = Format(sessionId),
                    ParticipantId = Format(displayName.Key),
                    DisplayName = displayName.Value
                },
                transaction,
                cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task CreateAsync(
        MediatedRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Status != MediatedRequestStatus.PendingDelivery ||
            request.ResolvedAt is not null ||
            string.IsNullOrWhiteSpace(request.Summary))
        {
            throw new ArgumentException(
                "A new mediated request must be pending delivery with a summary.",
                nameof(request));
        }

        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO MediatedRequests (
                Id, SessionId, RequesterId, RespondentId, Summary,
                Status, CreatedAt, ResolvedAt)
            VALUES (
                @Id, @SessionId, @RequesterId, @RespondentId, @Summary,
                @Status, @CreatedAt, NULL);
            """,
            new
            {
                Id = Format(request.Id),
                SessionId = Format(request.SessionId),
                RequesterId = Format(request.RequesterId),
                RespondentId = Format(request.RespondentId),
                request.Summary,
                Status = request.Status.ToString(),
                CreatedAt = Format(request.CreatedAt)
            },
            cancellationToken: cancellationToken));
    }

    public async Task MarkAwaitingResponseAsync(
        Guid sessionId,
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var affectedRows = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE MediatedRequests
            SET Status = 'AwaitingResponse'
            WHERE Id = @RequestId
              AND SessionId = @SessionId
              AND Status = 'PendingDelivery';
            """,
            new
            {
                RequestId = Format(requestId),
                SessionId = Format(sessionId)
            },
            cancellationToken: cancellationToken));
        EnsureSingleTransition(affectedRows, requestId, "PendingDelivery");
    }

    public async Task ResolveAsync(
        Guid sessionId,
        Guid requestId,
        MediatedRequestStatus status,
        DateTimeOffset resolvedAt,
        CancellationToken cancellationToken = default)
    {
        ValidateFinalStatus(status);
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var affectedRows = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE MediatedRequests
            SET Status = @Status, ResolvedAt = @ResolvedAt
            WHERE Id = @RequestId
              AND SessionId = @SessionId
              AND Status = 'AwaitingResponse';
            """,
            new
            {
                RequestId = Format(requestId),
                SessionId = Format(sessionId),
                Status = status.ToString(),
                ResolvedAt = Format(resolvedAt)
            },
            cancellationToken: cancellationToken));
        EnsureSingleTransition(affectedRows, requestId, "AwaitingResponse");
    }

    public async Task<IReadOnlyList<MediatedRequest>> GetOpenAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<MediatedRequestRow>(new CommandDefinition(
            """
            SELECT Id, SessionId, RequesterId, RespondentId, Summary,
                   Status, CreatedAt, ResolvedAt
            FROM MediatedRequests
            WHERE SessionId = @SessionId
              AND Status = 'AwaitingResponse'
            ORDER BY CreatedAt, Id;
            """,
            new { SessionId = Format(sessionId) },
            cancellationToken: cancellationToken));
        return rows.Select(ToMediatedRequest).ToArray();
    }

    public async Task<bool> TryRegisterAsync(
        string source,
        Guid sessionId,
        string externalUpdateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalUpdateId);
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var affectedRows = await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO ExternalUpdates (
                Source, SessionId, ExternalUpdateId, RegisteredAt)
            VALUES (
                @Source, @SessionId, @ExternalUpdateId, @RegisteredAt)
            ON CONFLICT (Source, SessionId, ExternalUpdateId) DO NOTHING;
            """,
            new
            {
                Source = source,
                SessionId = Format(sessionId),
                ExternalUpdateId = externalUpdateId,
                RegisteredAt = Format(DateTimeOffset.UtcNow)
            },
            cancellationToken: cancellationToken));

        return affectedRows == 1;
    }

    public async Task<bool> TryEnqueueAsync(
        PendingExternalTurn turn,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentException.ThrowIfNullOrWhiteSpace(turn.Source);
        ArgumentException.ThrowIfNullOrWhiteSpace(turn.ExternalUpdateId);
        ArgumentException.ThrowIfNullOrWhiteSpace(turn.ExternalUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(turn.Text);
        ArgumentOutOfRangeException.ThrowIfNegative(turn.SourceSequence);
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var registered = await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO ExternalUpdates (
                Source, SessionId, ExternalUpdateId, RegisteredAt)
            VALUES (
                @Source, @SessionId, @ExternalUpdateId, @RegisteredAt)
            ON CONFLICT (Source, SessionId, ExternalUpdateId) DO NOTHING;
            """,
            new
            {
                turn.Source,
                SessionId = Format(turn.SessionId),
                turn.ExternalUpdateId,
                RegisteredAt = Format(turn.CreatedAt)
            },
            transaction,
            cancellationToken: cancellationToken));
        if (registered == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO PendingTurns (
                Id, SessionId, ParticipantId, Source, ExternalUpdateId,
                SourceSequence, ExternalUserId, Text, CreatedAt)
            VALUES (
                @Id, @SessionId, @ParticipantId, @Source, @ExternalUpdateId,
                @SourceSequence, @ExternalUserId, @Text, @CreatedAt);
            """,
            new
            {
                Id = Format(turn.Id),
                SessionId = Format(turn.SessionId),
                ParticipantId = Format(turn.ParticipantId),
                turn.Source,
                turn.ExternalUpdateId,
                turn.SourceSequence,
                turn.ExternalUserId,
                turn.Text,
                CreatedAt = Format(turn.CreatedAt)
            },
            transaction,
            cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<PendingExternalTurn>> GetPendingAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<PendingExternalTurnRow>(new CommandDefinition(
            """
            SELECT Id, SessionId, ParticipantId, Source, ExternalUpdateId,
                   SourceSequence, ExternalUserId, Text, CreatedAt
            FROM PendingTurns
            WHERE SessionId = @SessionId
            ORDER BY SourceSequence, CreatedAt, Id;
            """,
            new { SessionId = Format(sessionId) },
            cancellationToken: cancellationToken));
        return rows.Select(ToPendingExternalTurn).ToArray();
    }

    public async Task CompleteAsync(
        Guid sessionId,
        Guid turnId,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var affectedRows = await connection.ExecuteAsync(new CommandDefinition(
            """
            DELETE FROM PendingTurns
            WHERE SessionId = @SessionId
              AND Id = @Id;
            """,
            new
            {
                SessionId = Format(sessionId),
                Id = Format(turnId)
            },
            cancellationToken: cancellationToken));
        if (affectedRows != 1)
        {
            throw new KeyNotFoundException(
                $"Pending turn '{turnId}' was not found in session '{sessionId}'.");
        }
    }

    public async Task EnsureBindingsAsync(
        Guid sessionId,
        string identityProvider,
        IReadOnlyCollection<ParticipantIdentityBinding> expectedBindings,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityProvider);
        ArgumentNullException.ThrowIfNull(expectedBindings);
        ValidateExpectedBindings(expectedBindings);
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var existingBindings = (await connection.QueryAsync<IdentityBindingRow>(
            new CommandDefinition(
                """
                SELECT ExternalId, ParticipantId
                FROM ParticipantIdentityBindings
                WHERE IdentityProvider = @IdentityProvider
                  AND SessionId = @SessionId;
                """,
                new
                {
                    IdentityProvider = identityProvider,
                    SessionId = Format(sessionId)
                },
                transaction,
                cancellationToken: cancellationToken))).ToArray();

        if (existingBindings.Length > 0)
        {
            var matches = existingBindings.Length == expectedBindings.Count &&
                expectedBindings.All(expected =>
                    existingBindings.Any(existing =>
                        existing.ExternalId == expected.ExternalId &&
                        existing.ParticipantId == Format(expected.ParticipantId)));
            if (!matches)
            {
                throw new InvalidOperationException(
                    $"Identity bindings for session '{sessionId}' do not match the configured participants.");
            }

            await transaction.CommitAsync(cancellationToken);
            return;
        }

        var externalIds = expectedBindings
            .Select(binding => binding.ExternalId)
            .ToArray();
        var conflictingBindingCount = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(
                """
                SELECT COUNT(*)
                FROM ParticipantIdentityBindings
                WHERE IdentityProvider = @IdentityProvider
                  AND ExternalId IN @ExternalIds;
                """,
                new
                {
                    IdentityProvider = identityProvider,
                    ExternalIds = externalIds
                },
                transaction,
                cancellationToken: cancellationToken));
        if (conflictingBindingCount > 0)
        {
            throw new InvalidOperationException(
                "An external identity is already bound to another participant.");
        }

        const string insertSql =
            """
            INSERT INTO ParticipantIdentityBindings (
                IdentityProvider, ExternalId, SessionId, ParticipantId)
            VALUES (
                @IdentityProvider, @ExternalId, @SessionId, @ParticipantId);
            """;
        foreach (var expected in expectedBindings)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                insertSql,
                new
                {
                    IdentityProvider = identityProvider,
                    expected.ExternalId,
                    SessionId = Format(sessionId),
                    ParticipantId = Format(expected.ParticipantId)
                },
                transaction,
                cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Message>> GetHistoryAsync(
        Guid sessionId,
        int? maxMessages = null,
        CancellationToken cancellationToken = default)
    {
        if (maxMessages is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxMessages));
        }

        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);

        var sql = maxMessages is null
            ? """
              SELECT Id, SessionId, AuthorId, RecipientId, Direction, Text, CreatedAt
              FROM Messages
              WHERE SessionId = @SessionId
              ORDER BY CreatedAt, Sequence;
              """
            : """
              WITH Recent AS (
                  SELECT Sequence, Id, SessionId, AuthorId, RecipientId,
                         Direction, Text, CreatedAt
                  FROM Messages
                  WHERE SessionId = @SessionId
                  ORDER BY CreatedAt DESC, Sequence DESC
                  LIMIT @MaxMessages
              )
              SELECT Id, SessionId, AuthorId, RecipientId, Direction, Text, CreatedAt
              FROM Recent
              ORDER BY CreatedAt, Sequence;
              """;

        var rows = await connection.QueryAsync<MessageRow>(new CommandDefinition(
            sql,
            new
            {
                SessionId = Format(sessionId),
                MaxMessages = maxMessages
            },
            cancellationToken: cancellationToken));

        return rows.Select(ToMessage).ToArray();
    }

    public async Task<ConversationSummary?> GetSummaryAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<ConversationSummaryRow>(
            new CommandDefinition(
                """
                SELECT SessionId, Version, CompactedThroughSequence,
                       PrivateContextFromParticipantA,
                       PrivateContextFromParticipantB,
                       SharedContextAndAgreements,
                       BoundariesAndSafety, UpdatedAt
                FROM ConversationSummaries
                WHERE SessionId = @SessionId;
                """,
                new { SessionId = Format(sessionId) },
                cancellationToken: cancellationToken));
        return row is null ? null : ToConversationSummary(row);
    }

    public async Task<ConversationCompactionBatch?> GetCompactionBatchAsync(
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
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        using var result = await connection.QueryMultipleAsync(new CommandDefinition(
            """
            SELECT SessionId, Version, CompactedThroughSequence,
                   PrivateContextFromParticipantA,
                   PrivateContextFromParticipantB,
                   SharedContextAndAgreements,
                   BoundariesAndSafety, UpdatedAt
            FROM ConversationSummaries
            WHERE SessionId = @SessionId;

            SELECT Sequence, Id, SessionId, AuthorId, RecipientId,
                   Direction, Text, CreatedAt
            FROM Messages
            WHERE SessionId = @SessionId
            ORDER BY Sequence;
            """,
            new { SessionId = Format(sessionId) },
            cancellationToken: cancellationToken));

        var summaryRow = await result.ReadSingleOrDefaultAsync<ConversationSummaryRow>();
        var messageRows = (await result.ReadAsync<MessageRow>()).ToArray();
        var totalCharacters = messageRows.Sum(row => row.Text.Length);
        if (messageRows.Length < triggerMessageCount &&
            totalCharacters < triggerCharacterCount)
        {
            return null;
        }

        var retainedCount = 0;
        var retainedCharacters = 0;
        for (var index = messageRows.Length - 1; index >= 0; index--)
        {
            var messageCharacters = messageRows[index].Text.Length;
            if (retainedCount >= retainRecentMessageCount ||
                (retainedCount > 0 &&
                 retainedCharacters + messageCharacters > retainRecentCharacterCount))
            {
                break;
            }

            retainedCount++;
            retainedCharacters += messageCharacters;
        }

        var compactedCount = messageRows.Length - retainedCount;
        if (compactedCount <= 0)
        {
            return null;
        }

        var compactedRows = messageRows.Take(compactedCount).ToArray();
        return new ConversationCompactionBatch(
            sessionId,
            summaryRow?.Version ?? 0,
            compactedRows[^1].Sequence,
            summaryRow is null ? null : ToSummaryContent(summaryRow),
            compactedRows
                .Select(row => new SequencedMessage(row.Sequence, ToMessage(row)))
                .ToArray());
    }

    public async Task CommitCompactionAsync(
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
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var currentVersion = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            """
            SELECT Version
            FROM ConversationSummaries
            WHERE SessionId = @SessionId;
            """,
            new { SessionId = Format(sessionId) },
            transaction,
            cancellationToken: cancellationToken));
        if ((currentVersion ?? 0) != expectedSummaryVersion)
        {
            throw new InvalidOperationException(
                "Conversation summary changed while compaction was running.");
        }

        var compactedMessageCount = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(
                """
                SELECT COUNT(*)
                FROM Messages
                WHERE SessionId = @SessionId
                  AND Sequence <= @CompactedThroughSequence;
                """,
                new
                {
                    SessionId = Format(sessionId),
                    CompactedThroughSequence = compactedThroughSequence
                },
                transaction,
                cancellationToken: cancellationToken));
        if (compactedMessageCount == 0)
        {
            throw new InvalidOperationException(
                "Compaction cutoff does not contain any conversation messages.");
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO ConversationSummaries (
                SessionId, Version, CompactedThroughSequence,
                PrivateContextFromParticipantA,
                PrivateContextFromParticipantB,
                SharedContextAndAgreements,
                BoundariesAndSafety, UpdatedAt)
            VALUES (
                @SessionId, @Version, @CompactedThroughSequence,
                @PrivateContextFromParticipantA,
                @PrivateContextFromParticipantB,
                @SharedContextAndAgreements,
                @BoundariesAndSafety, @UpdatedAt)
            ON CONFLICT (SessionId) DO UPDATE SET
                Version = excluded.Version,
                CompactedThroughSequence = excluded.CompactedThroughSequence,
                PrivateContextFromParticipantA = excluded.PrivateContextFromParticipantA,
                PrivateContextFromParticipantB = excluded.PrivateContextFromParticipantB,
                SharedContextAndAgreements = excluded.SharedContextAndAgreements,
                BoundariesAndSafety = excluded.BoundariesAndSafety,
                UpdatedAt = excluded.UpdatedAt;
            """,
            new
            {
                SessionId = Format(sessionId),
                Version = expectedSummaryVersion + 1,
                CompactedThroughSequence = compactedThroughSequence,
                summary.PrivateContextFromParticipantA,
                summary.PrivateContextFromParticipantB,
                summary.SharedContextAndAgreements,
                summary.BoundariesAndSafety,
                UpdatedAt = Format(updatedAt)
            },
            transaction,
            cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            """
            DELETE FROM Messages
            WHERE SessionId = @SessionId
              AND Sequence <= @CompactedThroughSequence;
            """,
            new
            {
                SessionId = Format(sessionId),
                CompactedThroughSequence = compactedThroughSequence
            },
            transaction,
            cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            await using var connection = await OpenConnectionAsync(cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(
                LoadSchema(),
                cancellationToken: cancellationToken));
            _initialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(
                """
                PRAGMA foreign_keys = ON;
                PRAGMA busy_timeout = 5000;
                PRAGMA cipher_memory_security = ON;
                """,
                cancellationToken: cancellationToken));
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static void EnsureSingleTransition(
        int affectedRows,
        Guid requestId,
        string expectedStatus)
    {
        if (affectedRows != 1)
        {
            throw new InvalidOperationException(
                $"Mediated request '{requestId}' was not in status '{expectedStatus}'.");
        }
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

    private static void ValidateDisplayNameValues(
        IReadOnlyDictionary<Guid, string> displayNames)
    {
        if (displayNames.Count == 0)
        {
            throw new ArgumentException(
                "At least one participant display name must be supplied.",
                nameof(displayNames));
        }

        foreach (var displayName in displayNames.Values)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
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

    private static void ValidateExpectedBindings(
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
        }
    }

    private static object ToParticipantParameters(
        Guid sessionId,
        Participant participant,
        int participantOrder) => new
        {
            Id = Format(participant.Id),
            SessionId = Format(sessionId),
            participant.DisplayName,
            ParticipantOrder = participantOrder
        };

    private static Participant ToParticipant(ParticipantRow row) =>
        new(Guid.Parse(row.Id), row.DisplayName);

    private static Message ToMessage(MessageRow row) => new(
        Guid.Parse(row.Id),
        Guid.Parse(row.SessionId),
        ParseGuid(row.AuthorId),
        ParseGuid(row.RecipientId),
        Enum.Parse<MessageDirection>(row.Direction),
        row.Text,
        ParseTimestamp(row.CreatedAt));

    private static ConversationSummary ToConversationSummary(
        ConversationSummaryRow row) => new(
        Guid.Parse(row.SessionId),
        row.Version,
        row.CompactedThroughSequence,
        ToSummaryContent(row),
        ParseTimestamp(row.UpdatedAt));

    private static ConversationSummaryContent ToSummaryContent(
        ConversationSummaryRow row) => new(
        row.PrivateContextFromParticipantA,
        row.PrivateContextFromParticipantB,
        row.SharedContextAndAgreements,
        row.BoundariesAndSafety);

    private static PendingExternalTurn ToPendingExternalTurn(
        PendingExternalTurnRow row) => new(
        Guid.Parse(row.Id),
        Guid.Parse(row.SessionId),
        Guid.Parse(row.ParticipantId),
        row.Source,
        row.ExternalUpdateId,
        row.SourceSequence,
        row.ExternalUserId,
        row.Text,
        ParseTimestamp(row.CreatedAt));

    private static MediatedRequest ToMediatedRequest(MediatedRequestRow row) => new(
        Guid.Parse(row.Id),
        Guid.Parse(row.SessionId),
        Guid.Parse(row.RequesterId),
        Guid.Parse(row.RespondentId),
        row.Summary,
        Enum.Parse<MediatedRequestStatus>(row.Status),
        ParseTimestamp(row.CreatedAt),
        row.ResolvedAt is null ? null : ParseTimestamp(row.ResolvedAt));

    private static string Format(Guid value) => value.ToString("D");

    private static string? Format(Guid? value) => value?.ToString("D");

    private static string Format(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static Guid? ParseGuid(string? value) =>
        value is null ? null : Guid.Parse(value);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

    private static string LoadSchema()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(SchemaResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded schema '{SchemaResourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed class SessionRow
    {
        public required string Id { get; init; }

        public required string CreatedAt { get; init; }
    }

    private sealed class ParticipantRow
    {
        public required string Id { get; init; }

        public required string DisplayName { get; init; }

        public int ParticipantOrder { get; init; }
    }

    private sealed class IdentityBindingRow
    {
        public required string ExternalId { get; init; }

        public required string ParticipantId { get; init; }
    }

    private sealed class MediatedRequestRow
    {
        public required string Id { get; init; }

        public required string SessionId { get; init; }

        public required string RequesterId { get; init; }

        public required string RespondentId { get; init; }

        public required string Summary { get; init; }

        public required string Status { get; init; }

        public required string CreatedAt { get; init; }

        public string? ResolvedAt { get; init; }
    }

    private sealed class PendingExternalTurnRow
    {
        public required string Id { get; init; }

        public required string SessionId { get; init; }

        public required string ParticipantId { get; init; }

        public required string Source { get; init; }

        public required string ExternalUpdateId { get; init; }

        public long SourceSequence { get; init; }

        public required string ExternalUserId { get; init; }

        public required string Text { get; init; }

        public required string CreatedAt { get; init; }
    }

    private sealed class ConversationSummaryRow
    {
        public required string SessionId { get; init; }

        public long Version { get; init; }

        public long CompactedThroughSequence { get; init; }

        public required string PrivateContextFromParticipantA { get; init; }

        public required string PrivateContextFromParticipantB { get; init; }

        public required string SharedContextAndAgreements { get; init; }

        public required string BoundariesAndSafety { get; init; }

        public required string UpdatedAt { get; init; }
    }

    private sealed class MessageRow
    {
        public long Sequence { get; init; }

        public required string Id { get; init; }

        public required string SessionId { get; init; }

        public string? AuthorId { get; init; }

        public string? RecipientId { get; init; }

        public required string Direction { get; init; }

        public required string Text { get; init; }

        public required string CreatedAt { get; init; }
    }
}
