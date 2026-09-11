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

CREATE TABLE IF NOT EXISTS ParticipantIdentityBindings (
    IdentityProvider TEXT NOT NULL,
    ExternalId TEXT NOT NULL,
    SessionId TEXT NOT NULL,
    ParticipantId TEXT NOT NULL,
    PRIMARY KEY (IdentityProvider, ExternalId),
    UNIQUE (IdentityProvider, SessionId, ParticipantId),
    FOREIGN KEY (SessionId) REFERENCES Sessions (Id) ON DELETE CASCADE,
    FOREIGN KEY (SessionId, ParticipantId) REFERENCES Participants (SessionId, Id)
);

CREATE TABLE IF NOT EXISTS ExternalUpdates (
    Source TEXT NOT NULL,
    SessionId TEXT NOT NULL,
    ExternalUpdateId TEXT NOT NULL,
    RegisteredAt TEXT NOT NULL,
    PRIMARY KEY (Source, SessionId, ExternalUpdateId),
    FOREIGN KEY (SessionId) REFERENCES Sessions (Id) ON DELETE CASCADE
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

CREATE TABLE IF NOT EXISTS MediatedRequests (
    Id TEXT NOT NULL PRIMARY KEY,
    SessionId TEXT NOT NULL,
    RequesterId TEXT NOT NULL,
    RespondentId TEXT NOT NULL,
    Summary TEXT NOT NULL,
    Status TEXT NOT NULL CHECK (
        Status IN (
            'PendingDelivery',
            'AwaitingResponse',
            'Answered',
            'Declined',
            'NoShareableAnswer',
            'Cancelled')),
    CreatedAt TEXT NOT NULL,
    ResolvedAt TEXT NULL,
    FOREIGN KEY (SessionId) REFERENCES Sessions (Id) ON DELETE CASCADE,
    FOREIGN KEY (SessionId, RequesterId) REFERENCES Participants (SessionId, Id),
    FOREIGN KEY (SessionId, RespondentId) REFERENCES Participants (SessionId, Id),
    CHECK (RequesterId <> RespondentId),
    CHECK (
        (Status IN ('PendingDelivery', 'AwaitingResponse') AND ResolvedAt IS NULL)
        OR
        (Status IN ('Answered', 'Declined', 'NoShareableAnswer', 'Cancelled')
            AND ResolvedAt IS NOT NULL)
    )
);

CREATE INDEX IF NOT EXISTS IX_MediatedRequests_SessionId_Status_CreatedAt
    ON MediatedRequests (SessionId, Status, CreatedAt);

PRAGMA user_version = 4;
