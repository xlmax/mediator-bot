ALTER TABLE PendingTurns
    ADD COLUMN Status TEXT NOT NULL DEFAULT 'Pending'
    CHECK (Status IN ('Pending', 'Failed'));
ALTER TABLE PendingTurns
    ADD COLUMN AttemptCount INTEGER NOT NULL DEFAULT 0
    CHECK (AttemptCount >= 0);
ALTER TABLE PendingTurns ADD COLUMN LastAttemptAt TEXT NULL;
ALTER TABLE PendingTurns ADD COLUMN FailedAt TEXT NULL;
ALTER TABLE PendingTurns ADD COLUMN FailureType TEXT NULL;

CREATE INDEX IX_PendingTurns_SessionId_Status_Order
    ON PendingTurns (SessionId, Status, SourceSequence, CreatedAt, Id);
