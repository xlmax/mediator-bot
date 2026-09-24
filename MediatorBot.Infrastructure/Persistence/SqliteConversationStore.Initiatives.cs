using System.Data.Common;
using Dapper;
using MediatorBot.Core;

namespace MediatorBot.Infrastructure;

public sealed partial class SqliteConversationStore
{
    public async Task<Guid?> GetLatestParticipantMessageIdAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var value = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            """
            SELECT Id
            FROM Messages
            WHERE SessionId = @SessionId
              AND Direction = 'ParticipantToMediator'
            ORDER BY Sequence DESC
            LIMIT 1;
            """,
            new { SessionId = Format(sessionId) },
            cancellationToken: cancellationToken));
        return value is null ? null : Guid.Parse(value);
    }

    public async Task<InitiativeDecision?> GetLatestDecisionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<InitiativeDecisionRow>(
            new CommandDefinition(
                DecisionSelect +
                """

                WHERE SessionId = @SessionId
                ORDER BY EvaluatedAt DESC, rowid DESC
                LIMIT 1;
                """,
                new { SessionId = Format(sessionId) },
                cancellationToken: cancellationToken));
        return row is null ? null : ToInitiativeDecision(row);
    }

    public async Task<IReadOnlyList<InitiativeDecision>> GetRecentDecisionsAsync(
        Guid sessionId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var rows = (await connection.QueryAsync<InitiativeDecisionRow>(
            new CommandDefinition(
                """
                SELECT * FROM (
                """ + DecisionSelect +
                """

                    WHERE SessionId = @SessionId
                    ORDER BY EvaluatedAt DESC, rowid DESC
                    LIMIT @Limit
                )
                ORDER BY EvaluatedAt, Id;
                """,
                new { SessionId = Format(sessionId), Limit = limit },
                cancellationToken: cancellationToken))).ToArray();
        return rows.Select(ToInitiativeDecision).ToArray();
    }

    public async Task<bool> TrySaveDecisionAsync(
        InitiativeDecision decision,
        Guid? expectedLatestDecisionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decision);
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var currentLatest = await connection.QuerySingleOrDefaultAsync<string>(
            new CommandDefinition(
                """
                SELECT Id
                FROM InitiativeDecisions
                WHERE SessionId = @SessionId
                ORDER BY EvaluatedAt DESC, rowid DESC
                LIMIT 1;
                """,
                new { SessionId = Format(decision.SessionId) },
                transaction,
                cancellationToken: cancellationToken));
        if (!string.Equals(
                currentLatest,
                expectedLatestDecisionId is null
                    ? null
                    : Format(expectedLatestDecisionId.Value),
                StringComparison.Ordinal))
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO InitiativeDecisions (
                Id, SessionId, ObservedParticipantMessageId, EvaluatedAt,
                Phase, Confidence, DecisionKind, TargetParticipantId,
                Intent, ReasonCode, OperationalRationale,
                TextForParticipantA, TextForParticipantB, NextEvaluationAt,
                PauseParticipantId, PauseUntil,
                Status, AttemptCount, DeliveredAt, FailedAt, FailureType)
            VALUES (
                @Id, @SessionId, @ObservedParticipantMessageId, @EvaluatedAt,
                @Phase, @Confidence, @DecisionKind, @TargetParticipantId,
                @Intent, @ReasonCode, @OperationalRationale,
                @TextForParticipantA, @TextForParticipantB, @NextEvaluationAt,
                @PauseParticipantId, @PauseUntil,
                @Status, @AttemptCount, @DeliveredAt, @FailedAt, @FailureType);
            """,
            ToInitiativeDecisionParameters(decision),
            transaction,
            cancellationToken: cancellationToken));
        if (decision.PauseParticipantId is Guid pauseParticipantId &&
            decision.PauseUntil is DateTimeOffset pauseUntil)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO InitiativeParticipantPreferences (
                    SessionId, ParticipantId, IsEnabled, PauseUntil, UpdatedAt)
                VALUES (@SessionId, @ParticipantId, 1, @PauseUntil, @UpdatedAt)
                ON CONFLICT (SessionId, ParticipantId) DO UPDATE SET
                    PauseUntil = CASE
                        WHEN PauseUntil IS NULL OR PauseUntil < excluded.PauseUntil
                        THEN excluded.PauseUntil ELSE PauseUntil END,
                    UpdatedAt = excluded.UpdatedAt;
                """,
                new
                {
                    SessionId = Format(decision.SessionId),
                    ParticipantId = Format(pauseParticipantId),
                    PauseUntil = Format(pauseUntil),
                    UpdatedAt = Format(decision.EvaluatedAt)
                },
                transaction,
                cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<InitiativeDecision>> GetPendingDeliveryDecisionsAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<InitiativeDecisionRow>(new CommandDefinition(
            DecisionSelect +
            """

            WHERE SessionId = @SessionId AND Status = 'PendingDelivery'
            ORDER BY EvaluatedAt, rowid;
            """,
            new { SessionId = Format(sessionId) },
            cancellationToken: cancellationToken));
        return rows.Select(ToInitiativeDecision).ToArray();
    }

    public async Task<int> BeginDecisionAttemptAsync(
        Guid sessionId,
        Guid decisionId,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE InitiativeDecisions
            SET AttemptCount = AttemptCount + 1
            WHERE Id = @DecisionId AND SessionId = @SessionId
              AND Status = 'PendingDelivery';
            """,
            new
            {
                DecisionId = Format(decisionId),
                SessionId = Format(sessionId)
            },
            cancellationToken: cancellationToken));
        return await connection.QuerySingleAsync<int>(new CommandDefinition(
            """
            SELECT AttemptCount
            FROM InitiativeDecisions
            WHERE Id = @DecisionId AND SessionId = @SessionId;
            """,
            new
            {
                DecisionId = Format(decisionId),
                SessionId = Format(sessionId)
            },
            cancellationToken: cancellationToken));
    }

    public async Task MarkDecisionStatusAsync(
        Guid sessionId,
        Guid decisionId,
        InitiativeDecisionStatus status,
        DateTimeOffset changedAt,
        string? failureType = null,
        CancellationToken cancellationToken = default)
    {
        if (status == InitiativeDecisionStatus.Failed !=
            !string.IsNullOrWhiteSpace(failureType))
        {
            throw new ArgumentException(
                "Failure type is required only for a failed initiative decision.",
                nameof(failureType));
        }

        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE InitiativeDecisions
            SET Status = @Status,
                DeliveredAt = CASE WHEN @Status = 'Delivered' THEN @ChangedAt ELSE NULL END,
                FailedAt = CASE WHEN @Status = 'Failed' THEN @ChangedAt ELSE NULL END,
                FailureType = CASE WHEN @Status = 'Failed' THEN @FailureType ELSE NULL END
            WHERE Id = @DecisionId AND SessionId = @SessionId
              AND Status = 'PendingDelivery';
            """,
            new
            {
                DecisionId = Format(decisionId),
                SessionId = Format(sessionId),
                Status = status.ToString(),
                ChangedAt = Format(changedAt),
                FailureType = failureType
            },
            cancellationToken: cancellationToken));
        if (affected == 0)
        {
            var existing = await GetInitiativeDecisionAsync(
                connection,
                sessionId,
                decisionId,
                cancellationToken: cancellationToken);
            if (existing.Status != status)
            {
                throw new InvalidOperationException(
                    $"Initiative decision '{decisionId}' could not transition to '{status}'.");
            }
        }
    }

    public async Task<int> CountDeliveredContactsAsync(
        Guid sessionId,
        Guid participantId,
        DateTimeOffset since,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleAsync<int>(new CommandDefinition(
            """
            SELECT COUNT(DISTINCT LogicalMessageId)
            FROM InitiativeDeliveries
            WHERE SessionId = @SessionId
              AND ParticipantId = @ParticipantId
              AND Status = 'Delivered'
              AND DeliveredAt >= @Since;
            """,
            new
            {
                SessionId = Format(sessionId),
                ParticipantId = Format(participantId),
                Since = Format(since)
            },
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<InitiativeParticipantPreference>>
        GetParticipantPreferencesAsync(
            Session session,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var rows = (await connection.QueryAsync<InitiativePreferenceRow>(
            new CommandDefinition(
                """
                SELECT SessionId, ParticipantId, IsEnabled, PauseUntil, UpdatedAt
                FROM InitiativeParticipantPreferences
                WHERE SessionId = @SessionId;
                """,
                new { SessionId = Format(session.Id) },
                cancellationToken: cancellationToken))).ToDictionary(
                    row => Guid.Parse(row.ParticipantId));
        return new[] { session.ParticipantA, session.ParticipantB }
            .Select(participant => rows.TryGetValue(participant.Id, out var row)
                ? ToInitiativePreference(row)
                : new InitiativeParticipantPreference(
                    session.Id,
                    participant.Id,
                    true,
                    null,
                    session.CreatedAt))
            .ToArray();
    }

    public Task SetParticipantEnabledAsync(
        Guid sessionId,
        Guid participantId,
        bool enabled,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken = default) =>
        UpsertPreferenceAsync(
            sessionId,
            participantId,
            enabled,
            null,
            changedAt,
            cancellationToken);

    public async Task PauseParticipantAsync(
        Guid sessionId,
        Guid participantId,
        DateTimeOffset pauseUntil,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO InitiativeParticipantPreferences (
                SessionId, ParticipantId, IsEnabled, PauseUntil, UpdatedAt)
            VALUES (@SessionId, @ParticipantId, 1, @PauseUntil, @UpdatedAt)
            ON CONFLICT (SessionId, ParticipantId) DO UPDATE SET
                PauseUntil = CASE
                    WHEN PauseUntil IS NULL OR PauseUntil < excluded.PauseUntil
                    THEN excluded.PauseUntil ELSE PauseUntil END,
                UpdatedAt = excluded.UpdatedAt;
            """,
            new
            {
                SessionId = Format(sessionId),
                ParticipantId = Format(participantId),
                PauseUntil = Format(pauseUntil),
                UpdatedAt = Format(changedAt)
            },
            cancellationToken: cancellationToken));
    }

    public async Task<bool> HasDeliveredInitiativeMessageAsync(
        Guid sessionId,
        Guid decisionId,
        Guid participantId,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            SELECT COUNT(*)
            FROM InitiativeDeliveries
            WHERE SessionId = @SessionId AND DecisionId = @DecisionId
              AND ParticipantId = @ParticipantId AND Status = 'Delivered';
            """,
            new
            {
                SessionId = Format(sessionId),
                DecisionId = Format(decisionId),
                ParticipantId = Format(participantId)
            },
            cancellationToken: cancellationToken)) > 0;
    }

    public async Task<IReadOnlyList<InitiativeDelivery>>
        EnsureInitiativeDeliveryPlanAsync(
            InitiativeDeliveryPlan plan,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Chunks.Count == 0 || plan.Chunks.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Initiative delivery plan requires chunks.", nameof(plan));
        }

        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var decision = await GetInitiativeDecisionAsync(
            connection,
            plan.SessionId,
            plan.DecisionId,
            transaction,
            cancellationToken);
        if (decision.Status != InitiativeDecisionStatus.PendingDelivery)
        {
            throw new InvalidOperationException(
                $"Initiative decision '{plan.DecisionId}' is not pending delivery.");
        }

        var existing = await GetInitiativeDeliveriesAsync(
            connection,
            plan.DecisionId,
            plan.DeliveryKey,
            transaction,
            cancellationToken);
        if (existing.Count > 0)
        {
            EnsureSameInitiativeDeliveryPlan(existing, plan);
            await transaction.CommitAsync(cancellationToken);
            return existing;
        }

        var logicalMessageId = Guid.NewGuid();
        for (var index = 0; index < plan.Chunks.Count; index++)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO InitiativeDeliveries (
                    Id, DecisionId, SessionId, ParticipantId, LogicalMessageId,
                    DeliveryKey, ChunkIndex, ChunkCount, Text, Status,
                    CreatedAt, AttemptedAt, DeliveredAt)
                VALUES (
                    @Id, @DecisionId, @SessionId, @ParticipantId, @LogicalMessageId,
                    @DeliveryKey, @ChunkIndex, @ChunkCount, @Text, 'Pending',
                    @CreatedAt, NULL, NULL);
                """,
                new
                {
                    Id = Format(Guid.NewGuid()),
                    DecisionId = Format(plan.DecisionId),
                    SessionId = Format(plan.SessionId),
                    ParticipantId = Format(plan.ParticipantId),
                    LogicalMessageId = Format(logicalMessageId),
                    plan.DeliveryKey,
                    ChunkIndex = index + 1,
                    ChunkCount = plan.Chunks.Count,
                    Text = plan.Chunks[index],
                    CreatedAt = Format(plan.CreatedAt)
                },
                transaction,
                cancellationToken: cancellationToken));
        }

        var created = await GetInitiativeDeliveriesAsync(
            connection,
            plan.DecisionId,
            plan.DeliveryKey,
            transaction,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return created;
    }

    public async Task MarkInitiativeDeliveryAttemptingAsync(
        Guid sessionId,
        Guid decisionId,
        Guid deliveryId,
        DateTimeOffset attemptedAt,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE InitiativeDeliveries
            SET Status = 'Attempting', AttemptedAt = @AttemptedAt
            WHERE Id = @DeliveryId AND DecisionId = @DecisionId
              AND SessionId = @SessionId AND Status <> 'Delivered';
            """,
            new
            {
                DeliveryId = Format(deliveryId),
                DecisionId = Format(decisionId),
                SessionId = Format(sessionId),
                AttemptedAt = Format(attemptedAt)
            },
            cancellationToken: cancellationToken));
        if (affected == 0)
        {
            var delivery = await GetInitiativeDeliveryAsync(
                connection,
                sessionId,
                decisionId,
                deliveryId,
                cancellationToken: cancellationToken);
            if (delivery.Status != InitiativeDeliveryStatus.Delivered)
            {
                throw new InvalidOperationException(
                    $"Initiative delivery '{deliveryId}' could not be marked attempting.");
            }
        }
    }

    public async Task RecordInitiativeDeliveryAsync(
        Guid sessionId,
        Guid decisionId,
        Guid deliveryId,
        DateTimeOffset deliveredAt,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var delivery = await GetInitiativeDeliveryAsync(
            connection,
            sessionId,
            decisionId,
            deliveryId,
            transaction,
            cancellationToken);
        if (delivery.Status == InitiativeDeliveryStatus.Delivered)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        if (delivery.Status != InitiativeDeliveryStatus.Attempting)
        {
            throw new InvalidOperationException(
                $"Initiative delivery '{deliveryId}' was not marked attempting.");
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE InitiativeDeliveries
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
        var deliveredChunks = (await connection.QueryAsync<InitiativeDeliveryRow>(
            new CommandDefinition(
                """
                SELECT Id, DecisionId, SessionId, ParticipantId, LogicalMessageId,
                       DeliveryKey, ChunkIndex, ChunkCount, Text, Status,
                       CreatedAt, AttemptedAt, DeliveredAt
                FROM InitiativeDeliveries
                WHERE LogicalMessageId = @LogicalMessageId AND Status = 'Delivered'
                ORDER BY ChunkIndex;
                """,
                new { LogicalMessageId = Format(delivery.LogicalMessageId) },
                transaction,
                cancellationToken: cancellationToken))).Select(
                    ToInitiativeDelivery).ToArray();
        var message = new Message(
            delivery.LogicalMessageId,
            sessionId,
            null,
            delivery.ParticipantId,
            MessageDirection.MediatorToParticipant,
            string.Concat(deliveredChunks.Select(chunk => chunk.Text)),
            deliveredChunks.Min(chunk => chunk.DeliveredAt)!.Value);
        var existing = await connection.QuerySingleOrDefaultAsync<MessageRow>(
            new CommandDefinition(
                """
                SELECT Sequence, Id, SessionId, AuthorId, RecipientId,
                       Direction, Text, CreatedAt
                FROM Messages WHERE Id = @Id;
                """,
                new { Id = Format(message.Id) },
                transaction,
                cancellationToken: cancellationToken));
        if (existing is null)
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
            var existingMessage = ToMessage(existing);
            if (existingMessage.SessionId != message.SessionId ||
                existingMessage.RecipientId != message.RecipientId ||
                existingMessage.Direction != MessageDirection.MediatorToParticipant)
            {
                throw new InvalidOperationException(
                    $"Initiative logical message '{message.Id}' has conflicting routing.");
            }

            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE Messages SET Text = @Text, CreatedAt = @CreatedAt
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

    private async Task UpsertPreferenceAsync(
        Guid sessionId,
        Guid participantId,
        bool enabled,
        DateTimeOffset? pauseUntil,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO InitiativeParticipantPreferences (
                SessionId, ParticipantId, IsEnabled, PauseUntil, UpdatedAt)
            VALUES (@SessionId, @ParticipantId, @IsEnabled, @PauseUntil, @UpdatedAt)
            ON CONFLICT (SessionId, ParticipantId) DO UPDATE SET
                IsEnabled = excluded.IsEnabled,
                PauseUntil = excluded.PauseUntil,
                UpdatedAt = excluded.UpdatedAt;
            """,
            new
            {
                SessionId = Format(sessionId),
                ParticipantId = Format(participantId),
                IsEnabled = enabled ? 1 : 0,
                PauseUntil = pauseUntil is null ? null : Format(pauseUntil.Value),
                UpdatedAt = Format(changedAt)
            },
            cancellationToken: cancellationToken));
    }

    private static object ToInitiativeDecisionParameters(InitiativeDecision decision) => new
    {
        Id = Format(decision.Id),
        SessionId = Format(decision.SessionId),
        ObservedParticipantMessageId = Format(decision.ObservedParticipantMessageId),
        EvaluatedAt = Format(decision.EvaluatedAt),
        Phase = decision.Phase.ToString(),
        Confidence = decision.Confidence.ToString(),
        DecisionKind = decision.DecisionKind.ToString(),
        TargetParticipantId = decision.TargetParticipantId is null
            ? null
            : Format(decision.TargetParticipantId.Value),
        Intent = decision.Intent.ToString(),
        ReasonCode = decision.ReasonCode.ToString(),
        decision.OperationalRationale,
        decision.TextForParticipantA,
        decision.TextForParticipantB,
        NextEvaluationAt = Format(decision.NextEvaluationAt),
        Status = decision.Status.ToString(),
        decision.AttemptCount,
        DeliveredAt = decision.DeliveredAt is null
            ? null
            : Format(decision.DeliveredAt.Value),
        FailedAt = decision.FailedAt is null ? null : Format(decision.FailedAt.Value),
        decision.FailureType,
        PauseParticipantId = decision.PauseParticipantId is null
            ? null
            : Format(decision.PauseParticipantId.Value),
        PauseUntil = decision.PauseUntil is null
            ? null
            : Format(decision.PauseUntil.Value)
    };

    private static InitiativeDecision ToInitiativeDecision(InitiativeDecisionRow row) => new(
        Guid.Parse(row.Id),
        Guid.Parse(row.SessionId),
        Guid.Parse(row.ObservedParticipantMessageId),
        ParseTimestamp(row.EvaluatedAt),
        Enum.Parse<RelationshipPhase>(row.Phase),
        Enum.Parse<InitiativeConfidence>(row.Confidence),
        Enum.Parse<InitiativeDecisionKind>(row.DecisionKind),
        row.TargetParticipantId is null ? null : Guid.Parse(row.TargetParticipantId),
        Enum.Parse<InitiativeIntent>(row.Intent),
        Enum.Parse<InitiativeReasonCode>(row.ReasonCode),
        row.OperationalRationale,
        row.TextForParticipantA,
        row.TextForParticipantB,
        ParseTimestamp(row.NextEvaluationAt),
        Enum.Parse<InitiativeDecisionStatus>(row.Status),
        row.AttemptCount,
        row.DeliveredAt is null ? null : ParseTimestamp(row.DeliveredAt),
        row.FailedAt is null ? null : ParseTimestamp(row.FailedAt),
        row.FailureType,
        row.PauseParticipantId is null ? null : Guid.Parse(row.PauseParticipantId),
        row.PauseUntil is null ? null : ParseTimestamp(row.PauseUntil));

    private static InitiativeParticipantPreference ToInitiativePreference(
        InitiativePreferenceRow row) => new(
        Guid.Parse(row.SessionId),
        Guid.Parse(row.ParticipantId),
        row.IsEnabled != 0,
        row.PauseUntil is null ? null : ParseTimestamp(row.PauseUntil),
        ParseTimestamp(row.UpdatedAt));

    private static InitiativeDelivery ToInitiativeDelivery(InitiativeDeliveryRow row) => new(
        Guid.Parse(row.Id),
        Guid.Parse(row.DecisionId),
        Guid.Parse(row.SessionId),
        Guid.Parse(row.ParticipantId),
        Guid.Parse(row.LogicalMessageId),
        row.DeliveryKey,
        row.ChunkIndex,
        row.ChunkCount,
        row.Text,
        Enum.Parse<InitiativeDeliveryStatus>(row.Status),
        ParseTimestamp(row.CreatedAt),
        row.AttemptedAt is null ? null : ParseTimestamp(row.AttemptedAt),
        row.DeliveredAt is null ? null : ParseTimestamp(row.DeliveredAt));

    private async Task<InitiativeDecision> GetInitiativeDecisionAsync(
        DbConnection connection,
        Guid sessionId,
        Guid decisionId,
        DbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        var row = await connection.QuerySingleOrDefaultAsync<InitiativeDecisionRow>(
            new CommandDefinition(
                DecisionSelect +
                """

                WHERE Id = @DecisionId AND SessionId = @SessionId;
                """,
                new
                {
                    DecisionId = Format(decisionId),
                    SessionId = Format(sessionId)
                },
                transaction,
                cancellationToken: cancellationToken)) ?? throw new KeyNotFoundException(
                    $"Initiative decision '{decisionId}' was not found.");
        return ToInitiativeDecision(row);
    }

    private static async Task<IReadOnlyList<InitiativeDelivery>>
        GetInitiativeDeliveriesAsync(
            DbConnection connection,
            Guid decisionId,
            string deliveryKey,
            DbTransaction? transaction,
            CancellationToken cancellationToken)
    {
        var rows = await connection.QueryAsync<InitiativeDeliveryRow>(new CommandDefinition(
            """
            SELECT Id, DecisionId, SessionId, ParticipantId, LogicalMessageId,
                   DeliveryKey, ChunkIndex, ChunkCount, Text, Status,
                   CreatedAt, AttemptedAt, DeliveredAt
            FROM InitiativeDeliveries
            WHERE DecisionId = @DecisionId AND DeliveryKey = @DeliveryKey
            ORDER BY ChunkIndex;
            """,
            new
            {
                DecisionId = Format(decisionId),
                DeliveryKey = deliveryKey
            },
            transaction,
            cancellationToken: cancellationToken));
        return rows.Select(ToInitiativeDelivery).ToArray();
    }

    private static async Task<InitiativeDelivery> GetInitiativeDeliveryAsync(
        DbConnection connection,
        Guid sessionId,
        Guid decisionId,
        Guid deliveryId,
        DbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        var row = await connection.QuerySingleOrDefaultAsync<InitiativeDeliveryRow>(
            new CommandDefinition(
                """
                SELECT Id, DecisionId, SessionId, ParticipantId, LogicalMessageId,
                       DeliveryKey, ChunkIndex, ChunkCount, Text, Status,
                       CreatedAt, AttemptedAt, DeliveredAt
                FROM InitiativeDeliveries
                WHERE Id = @DeliveryId AND DecisionId = @DecisionId
                  AND SessionId = @SessionId;
                """,
                new
                {
                    DeliveryId = Format(deliveryId),
                    DecisionId = Format(decisionId),
                    SessionId = Format(sessionId)
                },
                transaction,
                cancellationToken: cancellationToken)) ?? throw new KeyNotFoundException(
                    $"Initiative delivery '{deliveryId}' was not found.");
        return ToInitiativeDelivery(row);
    }

    private static void EnsureSameInitiativeDeliveryPlan(
        IReadOnlyList<InitiativeDelivery> existing,
        InitiativeDeliveryPlan plan)
    {
        if (existing.Count != plan.Chunks.Count ||
            existing.Any(delivery =>
                delivery.DecisionId != plan.DecisionId ||
                delivery.SessionId != plan.SessionId ||
                delivery.ParticipantId != plan.ParticipantId ||
                delivery.DeliveryKey != plan.DeliveryKey ||
                delivery.ChunkCount != plan.Chunks.Count ||
                delivery.Text != plan.Chunks[delivery.ChunkIndex - 1]))
        {
            throw new InvalidOperationException(
                $"Initiative delivery plan '{plan.DeliveryKey}' conflicts with persisted data.");
        }
    }

    private const string DecisionSelect =
        """
        SELECT Id, SessionId, ObservedParticipantMessageId, EvaluatedAt,
               Phase, Confidence, DecisionKind, TargetParticipantId,
               Intent, ReasonCode, OperationalRationale,
               TextForParticipantA, TextForParticipantB, NextEvaluationAt,
               PauseParticipantId, PauseUntil,
               Status, AttemptCount, DeliveredAt, FailedAt, FailureType
        FROM InitiativeDecisions
        """;

    private sealed class InitiativeDecisionRow
    {
        public required string Id { get; init; }
        public required string SessionId { get; init; }
        public required string ObservedParticipantMessageId { get; init; }
        public required string EvaluatedAt { get; init; }
        public required string Phase { get; init; }
        public required string Confidence { get; init; }
        public required string DecisionKind { get; init; }
        public string? TargetParticipantId { get; init; }
        public required string Intent { get; init; }
        public required string ReasonCode { get; init; }
        public required string OperationalRationale { get; init; }
        public string? TextForParticipantA { get; init; }
        public string? TextForParticipantB { get; init; }
        public required string NextEvaluationAt { get; init; }
        public string? PauseParticipantId { get; init; }
        public string? PauseUntil { get; init; }
        public required string Status { get; init; }
        public int AttemptCount { get; init; }
        public string? DeliveredAt { get; init; }
        public string? FailedAt { get; init; }
        public string? FailureType { get; init; }
    }

    private sealed class InitiativePreferenceRow
    {
        public required string SessionId { get; init; }
        public required string ParticipantId { get; init; }
        public long IsEnabled { get; init; }
        public string? PauseUntil { get; init; }
        public required string UpdatedAt { get; init; }
    }

    private sealed class InitiativeDeliveryRow
    {
        public required string Id { get; init; }
        public required string DecisionId { get; init; }
        public required string SessionId { get; init; }
        public required string ParticipantId { get; init; }
        public required string LogicalMessageId { get; init; }
        public required string DeliveryKey { get; init; }
        public int ChunkIndex { get; init; }
        public int ChunkCount { get; init; }
        public required string Text { get; init; }
        public required string Status { get; init; }
        public required string CreatedAt { get; init; }
        public string? AttemptedAt { get; init; }
        public string? DeliveredAt { get; init; }
    }
}
