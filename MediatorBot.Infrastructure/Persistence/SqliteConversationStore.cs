using System.Globalization;
using System.Reflection;
using Dapper;
using MediatorBot.Core;
using Microsoft.Data.Sqlite;

namespace MediatorBot.Infrastructure;

public sealed class SqliteConversationStore :
    IConversationStore,
    IParticipantIdentityStore,
    IExternalUpdateStore
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

    private sealed class MessageRow
    {
        public required string Id { get; init; }

        public required string SessionId { get; init; }

        public string? AuthorId { get; init; }

        public string? RecipientId { get; init; }

        public required string Direction { get; init; }

        public required string Text { get; init; }

        public required string CreatedAt { get; init; }
    }
}
