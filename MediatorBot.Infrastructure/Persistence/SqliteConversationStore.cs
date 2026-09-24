using System.Data.Common;
using System.Globalization;
using System.Reflection;
using Dapper;
using MediatorBot.Core;
using Microsoft.Data.Sqlite;

namespace MediatorBot.Infrastructure;

public sealed partial class SqliteConversationStore :
    IConversationStore,
    IParticipantIdentityStore,
    IExternalUpdateStore,
    IExternalTurnQueueStore,
    ITurnExecutionStore,
    IMediatedRequestStore,
    IConversationCompactionStore,
    IInitiativeStore,
    IInitiativeDeliveryStore
{
    private const int BaseSchemaVersion = 4;
    private const int CurrentSchemaVersion = 9;
    private const string SchemaResourceName =
        "MediatorBot.Infrastructure.Persistence.Schema.sql";

    private static readonly IReadOnlyList<(int Version, string ResourceName)> Migrations =
    [
        (5, "MediatorBot.Infrastructure.Persistence.Migrations.005_AddConversationSummaries.sql"),
        (6, "MediatorBot.Infrastructure.Persistence.Migrations.006_AddPendingTurns.sql"),
        (7, "MediatorBot.Infrastructure.Persistence.Migrations.007_AddReliableTurnExecution.sql"),
        (8, "MediatorBot.Infrastructure.Persistence.Migrations.008_AddFailedTurnQuarantine.sql"),
        (9, "MediatorBot.Infrastructure.Persistence.Migrations.009_AddProactiveInitiatives.sql")
    ];

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
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var pendingTurn = await connection.QuerySingleOrDefaultAsync<PendingIncomingRow>(
            new CommandDefinition(
                """
                SELECT SessionId, ParticipantId, Text, CreatedAt, IncomingRecordedAt
                FROM PendingTurns
                WHERE Id = @Id;
                """,
                new { Id = Format(message.Id) },
                transaction,
                cancellationToken: cancellationToken));
        if (pendingTurn?.IncomingRecordedAt is not null)
        {
            EnsureMatchesPendingTurn(message, pendingTurn);
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        if (pendingTurn is not null)
        {
            EnsureMatchesPendingTurn(message, pendingTurn);
            await RebindLegacyIncomingMessageAsync(
                connection,
                transaction,
                message,
                pendingTurn,
                cancellationToken);
        }

        var affectedRows = await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO Messages (
                Id, SessionId, AuthorId, RecipientId, Direction, Text, CreatedAt)
            VALUES (
                @Id, @SessionId, @AuthorId, @RecipientId, @Direction, @Text, @CreatedAt)
            ON CONFLICT (Id) DO NOTHING;
            """,
            ToMessageParameters(message),
            transaction,
            cancellationToken: cancellationToken));
        if (affectedRows == 0)
        {
            var existing = await connection.QuerySingleOrDefaultAsync<MessageRow>(
                new CommandDefinition(
                    """
                    SELECT Sequence, Id, SessionId, AuthorId, RecipientId,
                           Direction, Text, CreatedAt
                    FROM Messages
                    WHERE Id = @Id;
                    """,
                    new { Id = Format(message.Id) },
                    transaction,
                    cancellationToken: cancellationToken))
                ?? throw new InvalidOperationException(
                    $"Message '{message.Id}' conflicted but could not be loaded.");
            EnsureSameMessage(ToMessage(existing), message);
        }

        if (pendingTurn is not null)
        {
            EnsureMatchesPendingTurn(message, pendingTurn);
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE PendingTurns
                SET IncomingRecordedAt = @IncomingRecordedAt
                WHERE Id = @Id;
                """,
                new
                {
                    Id = Format(message.Id),
                    IncomingRecordedAt = Format(DateTimeOffset.UtcNow)
                },
                transaction,
                cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
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
        var affectedRows = await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO MediatedRequests (
                Id, SessionId, RequesterId, RespondentId, Summary,
                Status, CreatedAt, ResolvedAt)
            VALUES (
                @Id, @SessionId, @RequesterId, @RespondentId, @Summary,
                @Status, @CreatedAt, NULL)
            ON CONFLICT (Id) DO NOTHING;
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
        if (affectedRows == 0)
        {
            var existing = await GetMediatedRequestAsync(
                connection,
                request.SessionId,
                request.Id,
                cancellationToken);
            EnsureSameRequestIdentity(existing, request);
        }
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
        if (affectedRows == 0)
        {
            var existing = await GetMediatedRequestAsync(
                connection,
                sessionId,
                requestId,
                cancellationToken);
            if (existing.Status != MediatedRequestStatus.AwaitingResponse)
            {
                EnsureSingleTransition(affectedRows, requestId, "PendingDelivery");
            }
        }
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
        if (affectedRows == 0)
        {
            var existing = await GetMediatedRequestAsync(
                connection,
                sessionId,
                requestId,
                cancellationToken);
            if (existing.Status != status || existing.ResolvedAt is null)
            {
                EnsureSingleTransition(affectedRows, requestId, "AwaitingResponse");
            }
        }
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
                   SourceSequence, ExternalUserId, Text, CreatedAt, AttemptCount
            FROM PendingTurns
            WHERE SessionId = @SessionId
              AND Status = 'Pending'
            ORDER BY SourceSequence, CreatedAt, Id;
            """,
            new { SessionId = Format(sessionId) },
            cancellationToken: cancellationToken));
        return rows.Select(ToPendingExternalTurn).ToArray();
    }

    public async Task<int> BeginAttemptAsync(
        Guid sessionId,
        Guid turnId,
        DateTimeOffset attemptedAt,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var attemptCount = await connection.QuerySingleOrDefaultAsync<int?>(
            new CommandDefinition(
                """
                UPDATE PendingTurns
                SET AttemptCount = AttemptCount + 1,
                    LastAttemptAt = @AttemptedAt
                WHERE SessionId = @SessionId
                  AND Id = @TurnId
                  AND Status = 'Pending'
                RETURNING AttemptCount;
                """,
                new
                {
                    SessionId = Format(sessionId),
                    TurnId = Format(turnId),
                    AttemptedAt = Format(attemptedAt)
                },
                cancellationToken: cancellationToken));
        return attemptCount ?? throw new KeyNotFoundException(
            $"Pending turn '{turnId}' was not found in session '{sessionId}'.");
    }

    public async Task MarkFailedAsync(
        Guid sessionId,
        Guid turnId,
        string failureType,
        DateTimeOffset failedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureType);
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var affectedRows = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE PendingTurns
            SET Status = 'Failed',
                FailedAt = @FailedAt,
                FailureType = @FailureType
            WHERE SessionId = @SessionId
              AND Id = @TurnId
              AND Status = 'Pending';
            """,
            new
            {
                SessionId = Format(sessionId),
                TurnId = Format(turnId),
                FailedAt = Format(failedAt),
                FailureType = failureType
            },
            cancellationToken: cancellationToken));
        if (affectedRows != 1)
        {
            throw new KeyNotFoundException(
                $"Pending turn '{turnId}' was not found in session '{sessionId}'.");
        }
    }

    public async Task<int> GetFailedCountAsync(
        Guid sessionId,
        Guid participantId,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            SELECT COUNT(*)
            FROM PendingTurns
            WHERE SessionId = @SessionId
              AND ParticipantId = @ParticipantId
              AND Status = 'Failed';
            """,
            new
            {
                SessionId = Format(sessionId),
                ParticipantId = Format(participantId)
            },
            cancellationToken: cancellationToken));
    }

    public async Task<int> RetryFailedAsync(
        Guid sessionId,
        Guid participantId,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE PendingTurns
            SET Status = 'Pending',
                AttemptCount = 0,
                LastAttemptAt = NULL,
                FailedAt = NULL,
                FailureType = NULL
            WHERE SessionId = @SessionId
              AND ParticipantId = @ParticipantId
              AND Status = 'Failed';
            """,
            new
            {
                SessionId = Format(sessionId),
                ParticipantId = Format(participantId)
            },
            cancellationToken: cancellationToken));
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

    public async Task<string?> GetModelResultAsync(
        Guid sessionId,
        Guid turnId,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<ModelResultRow>(
            new CommandDefinition(
                """
                SELECT ModelResultJson
                FROM PendingTurns
                WHERE SessionId = @SessionId AND Id = @TurnId;
                """,
                new
                {
                    SessionId = Format(sessionId),
                    TurnId = Format(turnId)
                },
                cancellationToken: cancellationToken));
        return row?.ModelResultJson ?? (row is null
            ? throw new KeyNotFoundException(
                $"Pending turn '{turnId}' was not found in session '{sessionId}'.")
            : null);
    }

    public async Task SaveModelResultAsync(
        Guid sessionId,
        Guid turnId,
        string modelResultJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelResultJson);
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var affectedRows = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE PendingTurns
            SET ModelResultJson = @ModelResultJson
            WHERE SessionId = @SessionId
              AND Id = @TurnId
              AND ModelResultJson IS NULL;
            """,
            new
            {
                SessionId = Format(sessionId),
                TurnId = Format(turnId),
                ModelResultJson = modelResultJson
            },
            cancellationToken: cancellationToken));
        if (affectedRows == 1)
        {
            return;
        }

        var existing = await GetModelResultAsync(sessionId, turnId, cancellationToken);
        if (!string.Equals(existing, modelResultJson, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Turn '{turnId}' already has a different persisted model result.");
        }
    }

    public async Task<IReadOnlyList<TurnDelivery>> EnsureDeliveryPlanAsync(
        TurnDeliveryPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ValidateDeliveryPlan(plan);
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var pendingCount = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            SELECT COUNT(*)
            FROM PendingTurns
            WHERE SessionId = @SessionId AND Id = @TurnId;
            """,
            new
            {
                SessionId = Format(plan.SessionId),
                TurnId = Format(plan.TurnId)
            },
            transaction,
            cancellationToken: cancellationToken));
        if (pendingCount != 1)
        {
            throw new KeyNotFoundException(
                $"Pending turn '{plan.TurnId}' was not found in session '{plan.SessionId}'.");
        }

        var existing = (await connection.QueryAsync<TurnDeliveryRow>(new CommandDefinition(
            """
            SELECT Id, TurnId, SessionId, ParticipantId, LogicalMessageId,
                   DeliveryKey, ChunkIndex, ChunkCount, Text, ActionType,
                   DisclosureDecision, Status, CreatedAt, AttemptedAt, DeliveredAt
            FROM TurnDeliveries
            WHERE TurnId = @TurnId AND DeliveryKey = @DeliveryKey
            ORDER BY ChunkIndex;
            """,
            new
            {
                TurnId = Format(plan.TurnId),
                plan.DeliveryKey
            },
            transaction,
            cancellationToken: cancellationToken))).Select(ToTurnDelivery).ToArray();
        if (existing.Length > 0)
        {
            EnsureSameDeliveryPlan(existing, plan);
            await transaction.CommitAsync(cancellationToken);
            return existing;
        }

        var logicalMessageId = Guid.NewGuid();
        for (var index = 0; index < plan.Chunks.Count; index++)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO TurnDeliveries (
                    Id, TurnId, SessionId, ParticipantId, LogicalMessageId,
                    DeliveryKey, ChunkIndex, ChunkCount, Text, ActionType,
                    DisclosureDecision, Status, CreatedAt, AttemptedAt, DeliveredAt)
                VALUES (
                    @Id, @TurnId, @SessionId, @ParticipantId, @LogicalMessageId,
                    @DeliveryKey, @ChunkIndex, @ChunkCount, @Text, @ActionType,
                    @DisclosureDecision, 'Pending', @CreatedAt, NULL, NULL);
                """,
                new
                {
                    Id = Format(Guid.NewGuid()),
                    TurnId = Format(plan.TurnId),
                    SessionId = Format(plan.SessionId),
                    ParticipantId = Format(plan.ParticipantId),
                    LogicalMessageId = Format(logicalMessageId),
                    plan.DeliveryKey,
                    ChunkIndex = index + 1,
                    ChunkCount = plan.Chunks.Count,
                    Text = plan.Chunks[index],
                    plan.ActionType,
                    DisclosureDecision = plan.DisclosureDecision.ToString(),
                    CreatedAt = Format(plan.CreatedAt)
                },
                transaction,
                cancellationToken: cancellationToken));
        }

        var created = (await connection.QueryAsync<TurnDeliveryRow>(new CommandDefinition(
            """
            SELECT Id, TurnId, SessionId, ParticipantId, LogicalMessageId,
                   DeliveryKey, ChunkIndex, ChunkCount, Text, ActionType,
                   DisclosureDecision, Status, CreatedAt, AttemptedAt, DeliveredAt
            FROM TurnDeliveries
            WHERE TurnId = @TurnId AND DeliveryKey = @DeliveryKey
            ORDER BY ChunkIndex;
            """,
            new
            {
                TurnId = Format(plan.TurnId),
                plan.DeliveryKey
            },
            transaction,
            cancellationToken: cancellationToken))).Select(ToTurnDelivery).ToArray();
        await transaction.CommitAsync(cancellationToken);
        return created;
    }

    public async Task MarkDeliveryAttemptingAsync(
        Guid sessionId,
        Guid turnId,
        Guid deliveryId,
        DateTimeOffset attemptedAt,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var affectedRows = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE TurnDeliveries
            SET Status = 'Attempting', AttemptedAt = @AttemptedAt
            WHERE Id = @DeliveryId
              AND TurnId = @TurnId
              AND SessionId = @SessionId
              AND Status <> 'Delivered';
            """,
            new
            {
                DeliveryId = Format(deliveryId),
                TurnId = Format(turnId),
                SessionId = Format(sessionId),
                AttemptedAt = Format(attemptedAt)
            },
            cancellationToken: cancellationToken));
        if (affectedRows == 0)
        {
            var delivery = await GetTurnDeliveryAsync(
                connection,
                sessionId,
                turnId,
                deliveryId,
                cancellationToken: cancellationToken);
            if (delivery.Status != TurnDeliveryStatus.Delivered)
            {
                throw new InvalidOperationException(
                    $"Turn delivery '{deliveryId}' could not be marked as attempting.");
            }
        }
    }

    public async Task RecordDeliveryAsync(
        Guid sessionId,
        Guid turnId,
        Guid deliveryId,
        DateTimeOffset deliveredAt,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var delivery = await GetTurnDeliveryAsync(
            connection,
            sessionId,
            turnId,
            deliveryId,
            transaction,
            cancellationToken);
        if (delivery.Status == TurnDeliveryStatus.Delivered)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        if (delivery.Status != TurnDeliveryStatus.Attempting)
        {
            throw new InvalidOperationException(
                $"Turn delivery '{deliveryId}' was not marked as attempting.");
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE TurnDeliveries
            SET Status = 'Delivered', DeliveredAt = @DeliveredAt
            WHERE Id = @DeliveryId;
            """,
            new
            {
                DeliveryId = Format(deliveryId),
                DeliveredAt = Format(deliveredAt)
            },
            transaction,
            cancellationToken: cancellationToken));
        var deliveredChunks = (await connection.QueryAsync<TurnDeliveryRow>(
            new CommandDefinition(
                """
                SELECT Id, TurnId, SessionId, ParticipantId, LogicalMessageId,
                       DeliveryKey, ChunkIndex, ChunkCount, Text, ActionType,
                       DisclosureDecision, Status, CreatedAt, AttemptedAt, DeliveredAt
                FROM TurnDeliveries
                WHERE LogicalMessageId = @LogicalMessageId
                  AND Status = 'Delivered'
                ORDER BY ChunkIndex;
                """,
                new { LogicalMessageId = Format(delivery.LogicalMessageId) },
                transaction,
                cancellationToken: cancellationToken))).Select(ToTurnDelivery).ToArray();
        var message = new Message(
            delivery.LogicalMessageId,
            sessionId,
            null,
            delivery.ParticipantId,
            MessageDirection.MediatorToParticipant,
            string.Concat(deliveredChunks.Select(chunk => chunk.Text)),
            deliveredChunks.Min(chunk => chunk.DeliveredAt)!.Value);
        var existingMessage = await connection.QuerySingleOrDefaultAsync<MessageRow>(
            new CommandDefinition(
                """
                SELECT Sequence, Id, SessionId, AuthorId, RecipientId,
                       Direction, Text, CreatedAt
                FROM Messages
                WHERE Id = @Id;
                """,
                new { Id = Format(message.Id) },
                transaction,
                cancellationToken: cancellationToken));
        if (existingMessage is null)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO Messages (
                    Id, SessionId, AuthorId, RecipientId, Direction, Text, CreatedAt)
                VALUES (
                    @Id, @SessionId, @AuthorId, @RecipientId,
                    @Direction, @Text, @CreatedAt);
                """,
                ToMessageParameters(message),
                transaction,
                cancellationToken: cancellationToken));
        }
        else
        {
            var existing = ToMessage(existingMessage);
            if (existing.SessionId != message.SessionId ||
                existing.AuthorId is not null ||
                existing.RecipientId != message.RecipientId ||
                existing.Direction != MessageDirection.MediatorToParticipant)
            {
                throw new InvalidOperationException(
                    $"Logical message '{message.Id}' has conflicting routing data.");
            }

            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE Messages
                SET Text = @Text, CreatedAt = @CreatedAt
                WHERE Id = @Id;
                """,
                new
                {
                    Id = Format(message.Id),
                    message.Text,
                    CreatedAt = Format(message.CreatedAt)
                },
                transaction,
                cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
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
              ORDER BY Sequence;
              """
            : """
              WITH Recent AS (
                  SELECT Sequence, Id, SessionId, AuthorId, RecipientId,
                         Direction, Text, CreatedAt
                  FROM Messages
                  WHERE SessionId = @SessionId
                  ORDER BY Sequence DESC
                  LIMIT @MaxMessages
              )
              SELECT Id, SessionId, AuthorId, RecipientId, Direction, Text, CreatedAt
              FROM Recent
              ORDER BY Sequence;
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

            SELECT Id, ParticipantId, Text, CreatedAt, IncomingRecordedAt
            FROM PendingTurns
            WHERE SessionId = @SessionId
              AND Status = 'Pending';
            """,
            new { SessionId = Format(sessionId) },
            cancellationToken: cancellationToken));

        var summaryRow = await result.ReadSingleOrDefaultAsync<ConversationSummaryRow>();
        var messageRows = (await result.ReadAsync<MessageRow>()).ToArray();
        var pendingRows = (await result.ReadAsync<PendingCompactionRow>()).ToArray();
        var pendingMessageIds = pendingRows
            .Select(row => row.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var pendingRow in pendingRows.Where(row =>
                     row.IncomingRecordedAt is null))
        {
            var legacyMessage = messageRows.FirstOrDefault(messageRow =>
                messageRow.AuthorId == pendingRow.ParticipantId &&
                messageRow.RecipientId is null &&
                messageRow.Direction == nameof(MessageDirection.ParticipantToMediator) &&
                string.Equals(messageRow.Text, pendingRow.Text, StringComparison.Ordinal) &&
                ParseTimestamp(messageRow.CreatedAt) >=
                ParseTimestamp(pendingRow.CreatedAt));
            if (legacyMessage is not null)
            {
                pendingMessageIds.Add(legacyMessage.Id);
            }
        }

        var firstPendingIndex = Array.FindIndex(messageRows, row =>
            pendingMessageIds.Contains(row.Id));
        if (firstPendingIndex >= 0)
        {
            messageRows = messageRows.Take(firstPendingIndex).ToArray();
        }

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
            var version = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "PRAGMA user_version;",
                cancellationToken: cancellationToken));
            if (version > CurrentSchemaVersion)
            {
                throw new InvalidOperationException(
                    $"Database schema version {version} is newer than supported version " +
                    $"{CurrentSchemaVersion}.");
            }

            if (version < BaseSchemaVersion)
            {
                await using var transaction =
                    await connection.BeginTransactionAsync(cancellationToken);
                await connection.ExecuteAsync(new CommandDefinition(
                    LoadResource(SchemaResourceName),
                    transaction: transaction,
                    cancellationToken: cancellationToken));
                await SetSchemaVersionAsync(
                    connection,
                    transaction,
                    BaseSchemaVersion,
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                version = BaseSchemaVersion;
            }

            foreach (var migration in Migrations.Where(migration =>
                         migration.Version > version))
            {
                await using var transaction =
                    await connection.BeginTransactionAsync(cancellationToken);
                await connection.ExecuteAsync(new CommandDefinition(
                    LoadResource(migration.ResourceName),
                    transaction: transaction,
                    cancellationToken: cancellationToken));
                await SetSchemaVersionAsync(
                    connection,
                    transaction,
                    migration.Version,
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                version = migration.Version;
            }

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

    private static async Task<MediatedRequest> GetMediatedRequestAsync(
        SqliteConnection connection,
        Guid sessionId,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        var row = await connection.QuerySingleOrDefaultAsync<MediatedRequestRow>(
            new CommandDefinition(
                """
                SELECT Id, SessionId, RequesterId, RespondentId, Summary,
                       Status, CreatedAt, ResolvedAt
                FROM MediatedRequests
                WHERE Id = @RequestId AND SessionId = @SessionId;
                """,
                new
                {
                    RequestId = Format(requestId),
                    SessionId = Format(sessionId)
                },
                cancellationToken: cancellationToken));
        return row is null
            ? throw new KeyNotFoundException(
                $"Mediated request '{requestId}' was not found in session '{sessionId}'.")
            : ToMediatedRequest(row);
    }

    private static async Task<TurnDelivery> GetTurnDeliveryAsync(
        SqliteConnection connection,
        Guid sessionId,
        Guid turnId,
        Guid deliveryId,
        DbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        var row = await connection.QuerySingleOrDefaultAsync<TurnDeliveryRow>(
            new CommandDefinition(
                """
                SELECT Id, TurnId, SessionId, ParticipantId, LogicalMessageId,
                       DeliveryKey, ChunkIndex, ChunkCount, Text, ActionType,
                       DisclosureDecision, Status, CreatedAt, AttemptedAt, DeliveredAt
                FROM TurnDeliveries
                WHERE Id = @DeliveryId
                  AND TurnId = @TurnId
                  AND SessionId = @SessionId;
                """,
                new
                {
                    DeliveryId = Format(deliveryId),
                    TurnId = Format(turnId),
                    SessionId = Format(sessionId)
                },
                transaction,
                cancellationToken: cancellationToken));
        return row is null
            ? throw new KeyNotFoundException($"Turn delivery '{deliveryId}' was not found.")
            : ToTurnDelivery(row);
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

    private static void ValidateDeliveryPlan(TurnDeliveryPlan plan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.DeliveryKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.ActionType);
        if (plan.Chunks.Count == 0 || plan.Chunks.Any(string.IsNullOrWhiteSpace))
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
        if (existing.Any(delivery =>
                delivery.SessionId != plan.SessionId ||
                delivery.ParticipantId != plan.ParticipantId ||
                delivery.ChunkCount != existing.Count ||
                delivery.ActionType != plan.ActionType ||
                delivery.DisclosureDecision != plan.DisclosureDecision) ||
            !string.Equals(
                string.Concat(existing.Select(delivery => delivery.Text)),
                string.Concat(plan.Chunks),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Delivery plan '{plan.DeliveryKey}' conflicts with persisted deliveries.");
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

    private static object ToMessageParameters(Message message) => new
    {
        Id = Format(message.Id),
        SessionId = Format(message.SessionId),
        AuthorId = Format(message.AuthorId),
        RecipientId = Format(message.RecipientId),
        Direction = message.Direction.ToString(),
        message.Text,
        CreatedAt = Format(message.CreatedAt)
    };

    private static async Task RebindLegacyIncomingMessageAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        Message message,
        PendingIncomingRow pendingTurn,
        CancellationToken cancellationToken)
    {
        var legacySequence = await connection.QuerySingleOrDefaultAsync<long?>(
            new CommandDefinition(
                """
                SELECT Sequence
                FROM Messages
                WHERE Id <> @Id
                  AND SessionId = @SessionId
                  AND AuthorId = @AuthorId
                  AND RecipientId IS NULL
                  AND Direction = 'ParticipantToMediator'
                  AND Text = @Text
                  AND CreatedAt >= @TurnCreatedAt
                ORDER BY Sequence
                LIMIT 1;
                """,
                new
                {
                    Id = Format(message.Id),
                    SessionId = Format(message.SessionId),
                    AuthorId = Format(message.AuthorId),
                    message.Text,
                    TurnCreatedAt = pendingTurn.CreatedAt
                },
                transaction,
                cancellationToken: cancellationToken));
        if (legacySequence is null)
        {
            return;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE Messages
            SET Id = @Id,
                CreatedAt = @CreatedAt
            WHERE Sequence = @Sequence;
            """,
            new
            {
                Id = Format(message.Id),
                CreatedAt = Format(message.CreatedAt),
                Sequence = legacySequence.Value
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    private static void EnsureMatchesPendingTurn(
        Message message,
        PendingIncomingRow pendingTurn)
    {
        if (message.Direction != MessageDirection.ParticipantToMediator ||
            message.RecipientId is not null ||
            Format(message.SessionId) != pendingTurn.SessionId ||
            Format(message.AuthorId) != pendingTurn.ParticipantId ||
            !string.Equals(message.Text, pendingTurn.Text, StringComparison.Ordinal) ||
            Format(message.CreatedAt) != pendingTurn.CreatedAt)
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

    private static TurnDelivery ToTurnDelivery(TurnDeliveryRow row) => new(
        Guid.Parse(row.Id),
        Guid.Parse(row.TurnId),
        Guid.Parse(row.SessionId),
        Guid.Parse(row.ParticipantId),
        Guid.Parse(row.LogicalMessageId),
        row.DeliveryKey,
        row.ChunkIndex,
        row.ChunkCount,
        row.Text,
        row.ActionType,
        Enum.Parse<DisclosureDecision>(row.DisclosureDecision),
        Enum.Parse<TurnDeliveryStatus>(row.Status),
        ParseTimestamp(row.CreatedAt),
        row.AttemptedAt is null ? null : ParseTimestamp(row.AttemptedAt),
        row.DeliveredAt is null ? null : ParseTimestamp(row.DeliveredAt));

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
        ParseTimestamp(row.CreatedAt),
        row.AttemptCount);

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

    private static async Task SetSchemaVersionAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        int version,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            $"PRAGMA user_version = {version};",
            transaction: transaction,
            cancellationToken: cancellationToken));
    }

    private static string LoadResource(string resourceName)
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded SQL resource '{resourceName}' was not found.");
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

    private sealed class ModelResultRow
    {
        public string? ModelResultJson { get; init; }
    }

    private sealed class TurnDeliveryRow
    {
        public required string Id { get; init; }

        public required string TurnId { get; init; }

        public required string SessionId { get; init; }

        public required string ParticipantId { get; init; }

        public required string LogicalMessageId { get; init; }

        public required string DeliveryKey { get; init; }

        public int ChunkIndex { get; init; }

        public int ChunkCount { get; init; }

        public required string Text { get; init; }

        public required string ActionType { get; init; }

        public required string DisclosureDecision { get; init; }

        public required string Status { get; init; }

        public required string CreatedAt { get; init; }

        public string? AttemptedAt { get; init; }

        public string? DeliveredAt { get; init; }
    }

    private sealed class PendingIncomingRow
    {
        public required string SessionId { get; init; }

        public required string ParticipantId { get; init; }

        public required string Text { get; init; }

        public required string CreatedAt { get; init; }

        public string? IncomingRecordedAt { get; init; }
    }

    private sealed class PendingCompactionRow
    {
        public required string Id { get; init; }

        public required string ParticipantId { get; init; }

        public required string Text { get; init; }

        public required string CreatedAt { get; init; }

        public string? IncomingRecordedAt { get; init; }
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

        public int AttemptCount { get; init; }
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
