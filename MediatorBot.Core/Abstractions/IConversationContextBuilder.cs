namespace MediatorBot.Core;

public interface IConversationContextBuilder
{
    Task<ConversationContext> BuildAsync(
        Session session,
        Message incomingMessage,
        CancellationToken cancellationToken = default);
}
