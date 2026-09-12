CREATE TABLE PendingTurns (
    Id TEXT NOT NULL PRIMARY KEY,
    SessionId TEXT NOT NULL,
    ParticipantId TEXT NOT NULL,
    Source TEXT NOT NULL,
    ExternalUpdateId TEXT NOT NULL,
    SourceSequence INTEGER NOT NULL,
    ExternalUserId TEXT NOT NULL,
    Text TEXT NOT NULL,
    CreatedAt TEXT NOT NULL,
    FOREIGN KEY (SessionId) REFERENCES Sessions (Id) ON DELETE CASCADE,
    FOREIGN KEY (SessionId, ParticipantId) REFERENCES Participants (SessionId, Id),
    UNIQUE (Source, SessionId, ExternalUpdateId)
);

CREATE INDEX IX_PendingTurns_SessionId_SourceSequence
    ON PendingTurns (SessionId, SourceSequence, CreatedAt, Id);
