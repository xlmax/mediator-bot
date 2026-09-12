ALTER TABLE PendingTurns ADD COLUMN IncomingRecordedAt TEXT NULL;
ALTER TABLE PendingTurns ADD COLUMN ModelResultJson TEXT NULL;

CREATE TABLE TurnDeliveries (
    Id TEXT NOT NULL PRIMARY KEY,
    TurnId TEXT NOT NULL,
    SessionId TEXT NOT NULL,
    ParticipantId TEXT NOT NULL,
    LogicalMessageId TEXT NOT NULL,
    DeliveryKey TEXT NOT NULL,
    ChunkIndex INTEGER NOT NULL CHECK (ChunkIndex > 0),
    ChunkCount INTEGER NOT NULL CHECK (ChunkCount > 0),
    Text TEXT NOT NULL,
    ActionType TEXT NOT NULL,
    DisclosureDecision TEXT NOT NULL,
    Status TEXT NOT NULL CHECK (Status IN ('Pending', 'Attempting', 'Delivered')),
    CreatedAt TEXT NOT NULL,
    AttemptedAt TEXT NULL,
    DeliveredAt TEXT NULL,
    FOREIGN KEY (TurnId) REFERENCES PendingTurns (Id) ON DELETE CASCADE,
    FOREIGN KEY (SessionId) REFERENCES Sessions (Id) ON DELETE CASCADE,
    FOREIGN KEY (SessionId, ParticipantId) REFERENCES Participants (SessionId, Id),
    UNIQUE (TurnId, DeliveryKey, ChunkIndex),
    CHECK (ChunkIndex <= ChunkCount),
    CHECK (
        (Status = 'Pending' AND AttemptedAt IS NULL AND DeliveredAt IS NULL)
        OR
        (Status = 'Attempting' AND AttemptedAt IS NOT NULL AND DeliveredAt IS NULL)
        OR
        (Status = 'Delivered' AND AttemptedAt IS NOT NULL AND DeliveredAt IS NOT NULL)
    )
);

CREATE INDEX IX_TurnDeliveries_TurnId_DeliveryKey_ChunkIndex
    ON TurnDeliveries (TurnId, DeliveryKey, ChunkIndex);
