CREATE TABLE IF NOT EXISTS InitiativeParticipantPreferences (
    SessionId TEXT NOT NULL,
    ParticipantId TEXT NOT NULL,
    IsEnabled INTEGER NOT NULL DEFAULT 1 CHECK (IsEnabled IN (0, 1)),
    PauseUntil TEXT NULL,
    UpdatedAt TEXT NOT NULL,
    PRIMARY KEY (SessionId, ParticipantId),
    FOREIGN KEY (SessionId) REFERENCES Sessions (Id) ON DELETE CASCADE,
    FOREIGN KEY (SessionId, ParticipantId)
        REFERENCES Participants (SessionId, Id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS InitiativeDecisions (
    Id TEXT NOT NULL PRIMARY KEY,
    SessionId TEXT NOT NULL,
    ObservedParticipantMessageId TEXT NOT NULL,
    EvaluatedAt TEXT NOT NULL,
    Phase TEXT NOT NULL CHECK (
        Phase IN ('Calm', 'Tension', 'ActiveConflict', 'CoolingDown',
                  'RepairWindow', 'Uncertain')),
    Confidence TEXT NOT NULL CHECK (Confidence IN ('Low', 'Medium', 'High')),
    DecisionKind TEXT NOT NULL CHECK (
        DecisionKind IN ('NoAction', 'ReevaluateLater',
                         'ContactParticipant', 'ContactBoth')),
    TargetParticipantId TEXT NULL,
    Intent TEXT NOT NULL CHECK (
        Intent IN ('Observe', 'CheckIn', 'AssessReadiness',
                   'SupportRepair', 'Bridge', 'ConfirmPositiveState')),
    ReasonCode TEXT NOT NULL CHECK (
        ReasonCode IN ('NoUsefulAction', 'RecentConflict', 'RequestedSpace',
                       'RecentInitiative', 'RepairOpportunity',
                       'PositiveStateUncertain', 'ParticipantPreference',
                       'SafetyRisk', 'Other')),
    OperationalRationale TEXT NOT NULL CHECK (
        length(OperationalRationale) BETWEEN 1 AND 500),
    TextForParticipantA TEXT NULL,
    TextForParticipantB TEXT NULL,
    NextEvaluationAt TEXT NOT NULL,
    PauseParticipantId TEXT NULL,
    PauseUntil TEXT NULL,
    Status TEXT NOT NULL CHECK (
        Status IN ('NoDelivery', 'Shadow', 'PendingDelivery', 'Delivered',
                   'Superseded', 'Suppressed', 'Failed')),
    AttemptCount INTEGER NOT NULL DEFAULT 0 CHECK (AttemptCount >= 0),
    DeliveredAt TEXT NULL,
    FailedAt TEXT NULL,
    FailureType TEXT NULL,
    FOREIGN KEY (SessionId) REFERENCES Sessions (Id) ON DELETE CASCADE,
    FOREIGN KEY (SessionId, TargetParticipantId)
        REFERENCES Participants (SessionId, Id),
    FOREIGN KEY (SessionId, PauseParticipantId)
        REFERENCES Participants (SessionId, Id),
    CHECK (
        (PauseParticipantId IS NULL AND PauseUntil IS NULL)
        OR (PauseParticipantId IS NOT NULL AND PauseUntil IS NOT NULL)),
    CHECK (
        (DecisionKind = 'ContactParticipant' AND TargetParticipantId IS NOT NULL)
        OR (DecisionKind <> 'ContactParticipant' AND TargetParticipantId IS NULL)),
    CHECK (
        (Status = 'Delivered' AND DeliveredAt IS NOT NULL)
        OR (Status <> 'Delivered' AND DeliveredAt IS NULL)),
    CHECK (
        (Status = 'Failed' AND FailedAt IS NOT NULL AND FailureType IS NOT NULL)
        OR (Status <> 'Failed' AND FailedAt IS NULL AND FailureType IS NULL))
);

CREATE INDEX IF NOT EXISTS IX_InitiativeDecisions_SessionId_EvaluatedAt
    ON InitiativeDecisions (SessionId, EvaluatedAt, Id);
CREATE INDEX IF NOT EXISTS IX_InitiativeDecisions_SessionId_Status
    ON InitiativeDecisions (SessionId, Status, EvaluatedAt, Id);

CREATE TABLE IF NOT EXISTS InitiativeDeliveries (
    Id TEXT NOT NULL PRIMARY KEY,
    DecisionId TEXT NOT NULL,
    SessionId TEXT NOT NULL,
    ParticipantId TEXT NOT NULL,
    LogicalMessageId TEXT NOT NULL,
    DeliveryKey TEXT NOT NULL,
    ChunkIndex INTEGER NOT NULL CHECK (ChunkIndex > 0),
    ChunkCount INTEGER NOT NULL CHECK (ChunkCount > 0),
    Text TEXT NOT NULL,
    Status TEXT NOT NULL CHECK (Status IN ('Pending', 'Attempting', 'Delivered')),
    CreatedAt TEXT NOT NULL,
    AttemptedAt TEXT NULL,
    DeliveredAt TEXT NULL,
    FOREIGN KEY (DecisionId) REFERENCES InitiativeDecisions (Id) ON DELETE CASCADE,
    FOREIGN KEY (SessionId) REFERENCES Sessions (Id) ON DELETE CASCADE,
    FOREIGN KEY (SessionId, ParticipantId)
        REFERENCES Participants (SessionId, Id),
    UNIQUE (DecisionId, DeliveryKey, ChunkIndex),
    CHECK (ChunkIndex <= ChunkCount),
    CHECK (
        (Status = 'Pending' AND AttemptedAt IS NULL AND DeliveredAt IS NULL)
        OR (Status = 'Attempting' AND AttemptedAt IS NOT NULL AND DeliveredAt IS NULL)
        OR (Status = 'Delivered' AND AttemptedAt IS NOT NULL AND DeliveredAt IS NOT NULL))
);

CREATE INDEX IF NOT EXISTS IX_InitiativeDeliveries_DecisionId_DeliveryKey_ChunkIndex
    ON InitiativeDeliveries (DecisionId, DeliveryKey, ChunkIndex);
CREATE INDEX IF NOT EXISTS IX_InitiativeDeliveries_SessionId_ParticipantId_DeliveredAt
    ON InitiativeDeliveries (SessionId, ParticipantId, DeliveredAt);
