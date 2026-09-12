CREATE TABLE ConversationSummaries (
    SessionId TEXT NOT NULL PRIMARY KEY,
    Version INTEGER NOT NULL CHECK (Version > 0),
    CompactedThroughSequence INTEGER NOT NULL CHECK (CompactedThroughSequence > 0),
    PrivateContextFromParticipantA TEXT NOT NULL,
    PrivateContextFromParticipantB TEXT NOT NULL,
    SharedContextAndAgreements TEXT NOT NULL,
    BoundariesAndSafety TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    FOREIGN KEY (SessionId) REFERENCES Sessions (Id) ON DELETE CASCADE
);
