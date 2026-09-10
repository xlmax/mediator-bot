namespace MediatorBot.Core;

public interface IConversationStore
{
    Task CreateSessionAsync(
        Session session,
        CancellationToken cancellationToken = default);

    Task<Session?> GetSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);

    Task SaveMessageAsync(
        Message message,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Message>> GetHistoryAsync(
        Guid sessionId,
        int? maxMessages = null,
        CancellationToken cancellationToken = default);
}
