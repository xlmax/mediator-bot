PRAGMA journal_mode = WAL;

CREATE TABLE IF NOT EXISTS Sessions (
    Id TEXT NOT NULL PRIMARY KEY,
    CreatedAt TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS Participants (
    Id TEXT NOT NULL PRIMARY KEY,
    SessionId TEXT NOT NULL,
    DisplayName TEXT NOT NULL,
    ParticipantOrder INTEGER NOT NULL CHECK (ParticipantOrder IN (0, 1)),
    FOREIGN KEY (SessionId) REFERENCES Sessions (Id) ON DELETE CASCADE,
    UNIQUE (SessionId, ParticipantOrder),
    UNIQUE (SessionId, Id)
);

CREATE TABLE IF NOT EXISTS Messages (
    Sequence INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    Id TEXT NOT NULL UNIQUE,
    SessionId TEXT NOT NULL,
    AuthorId TEXT NULL,
    RecipientId TEXT NULL,
    Direction TEXT NOT NULL CHECK (
        Direction IN ('ParticipantToMediator', 'MediatorToParticipant')),
    Text TEXT NOT NULL,
    CreatedAt TEXT NOT NULL,
    FOREIGN KEY (SessionId) REFERENCES Sessions (Id) ON DELETE CASCADE,
    FOREIGN KEY (SessionId, AuthorId) REFERENCES Participants (SessionId, Id),
    FOREIGN KEY (SessionId, RecipientId) REFERENCES Participants (SessionId, Id),
    CHECK (
        (Direction = 'ParticipantToMediator' AND AuthorId IS NOT NULL AND RecipientId IS NULL)
        OR
        (Direction = 'MediatorToParticipant' AND AuthorId IS NULL AND RecipientId IS NOT NULL)
    )
);

CREATE INDEX IF NOT EXISTS IX_Messages_SessionId_CreatedAt
    ON Messages (SessionId, CreatedAt, Sequence);

PRAGMA user_version = 1;
