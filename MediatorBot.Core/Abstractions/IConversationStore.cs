namespace MediatorBot.Core;

public interface IConversationStore
{
    Task<Session?> GetSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);

    Task SaveMessageAsync(
        Message message,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Message>> GetHistoryAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);
}
