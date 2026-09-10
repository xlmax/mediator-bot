namespace MediatorBot.Infrastructure;

public sealed record SqliteConversationStoreOptions(
    string DatabasePath,
    string DatabaseKey);
