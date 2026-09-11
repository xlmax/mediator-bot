using MediatorBot.Core;

namespace MediatorBot.Infrastructure;

public sealed class DisabledConversationSummaryGenerator : IConversationSummaryGenerator
{
    public Task<ConversationSummaryContent> GenerateAsync(
        Session session,
        ConversationSummaryContent? previousSummary,
        IReadOnlyList<SequencedMessage> messages,
        int maxSummaryCharacters,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(
            "Conversation compaction requires an OpenAI model runtime.");
}
