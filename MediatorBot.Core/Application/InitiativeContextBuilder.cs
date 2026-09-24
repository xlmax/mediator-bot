namespace MediatorBot.Core;

public sealed class InitiativeContextBuilder(
    IConversationStore conversationStore,
    IMediatedRequestStore mediatedRequestStore,
    IConversationCompactionStore compactionStore,
    IInitiativeStore initiativeStore,
    InitiativeOptions options) : IInitiativeContextBuilder
{
    public async Task<InitiativeContext?> BuildAsync(
        Guid sessionId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var session = await conversationStore.GetSessionAsync(sessionId, cancellationToken)
            ?? throw new KeyNotFoundException($"Session '{sessionId}' was not found.");
        var historyTask = conversationStore.GetHistoryAsync(
            sessionId,
            options.MaxHistoryMessages,
            cancellationToken);
        var summaryTask = compactionStore.GetSummaryAsync(sessionId, cancellationToken);
        var openRequestsTask = mediatedRequestStore.GetOpenAsync(sessionId, cancellationToken);
        var decisionsTask = initiativeStore.GetRecentDecisionsAsync(
            sessionId,
            options.RecentDecisionCount,
            cancellationToken);
        var preferencesTask = initiativeStore.GetParticipantPreferencesAsync(
            session,
            cancellationToken);
        var participantACountTask = initiativeStore.CountDeliveredContactsAsync(
            sessionId,
            session.ParticipantA.Id,
            now.AddHours(-24),
            cancellationToken);
        var participantBCountTask = initiativeStore.CountDeliveredContactsAsync(
            sessionId,
            session.ParticipantB.Id,
            now.AddHours(-24),
            cancellationToken);
        await Task.WhenAll(
            historyTask,
            summaryTask,
            openRequestsTask,
            decisionsTask,
            preferencesTask,
            participantACountTask,
            participantBCountTask);

        var history = await historyTask;
        var latestParticipantMessage = history.LastOrDefault(
            message => message.Direction == MessageDirection.ParticipantToMediator);
        if (latestParticipantMessage is null)
        {
            return null;
        }

        return new InitiativeContext(
            session,
            history,
            latestParticipantMessage,
            now,
            await decisionsTask,
            await preferencesTask,
            new Dictionary<Guid, int>
            {
                [session.ParticipantA.Id] = await participantACountTask,
                [session.ParticipantB.Id] = await participantBCountTask
            })
        {
            Summary = await summaryTask,
            OpenMediatedRequests = await openRequestsTask
        };
    }
}
