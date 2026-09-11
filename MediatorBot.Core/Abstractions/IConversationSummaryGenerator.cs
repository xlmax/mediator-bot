namespace MediatorBot.Core;

public interface IConversationSummaryGenerator
{
    Task<ConversationSummaryContent> GenerateAsync(
        Session session,
        ConversationSummaryContent? previousSummary,
        IReadOnlyList<SequencedMessage> messages,
        int maxSummaryCharacters,
        CancellationToken cancellationToken = default);
}
