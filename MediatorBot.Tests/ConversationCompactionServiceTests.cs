using MediatorBot.Core;
using MediatorBot.Infrastructure;

namespace MediatorBot.Tests;

public sealed class ConversationCompactionServiceTests
{
    [Fact]
    public async Task ExecuteAsync_ReplacesSummaryAndDeletesOnlyCompactedMessages()
    {
        var fixture = CreateFixture();
        await AddMessagesAsync(fixture, "one", "two", "three", "four");

        var plan = await fixture.Service.PrepareAsync(fixture.Session.Id);
        Assert.NotNull(plan);
        Assert.Equal(["one", "two"], plan.Batch.Messages.Select(entry => entry.Message.Text));

        var result = await fixture.Service.ExecuteAsync(plan);

        Assert.Equal(2, result.CompactedMessageCount);
        Assert.Equal(
            ["three", "four"],
            (await fixture.Store.GetHistoryAsync(fixture.Session.Id))
                .Select(message => message.Text));
        var summary = await fixture.Store.GetSummaryAsync(fixture.Session.Id);
        Assert.NotNull(summary);
        Assert.Equal(1, summary.Version);
        Assert.Equal("private-a", summary.Content.PrivateContextFromParticipantA);
    }

    [Fact]
    public async Task PrepareAsync_UsesCharacterTriggerAndPreservesNewestMessage()
    {
        var fixture = CreateFixture(new ConversationCompactionOptions
        {
            Enabled = true,
            TriggerMessageCount = 10,
            TriggerHistoryCharacters = 10,
            RetainRecentMessageCount = 5,
            RetainRecentCharacters = 5,
            MaxSummaryCharacters = 100,
            MaxOutputTokens = 100,
            OperationTimeout = TimeSpan.FromSeconds(5),
            RetryDelay = TimeSpan.FromSeconds(1)
        });
        await AddMessagesAsync(fixture, "aaaa", "bbbb", "cccc");

        var plan = await fixture.Service.PrepareAsync(fixture.Session.Id);

        Assert.NotNull(plan);
        Assert.Equal(["aaaa", "bbbb"], plan.Batch.Messages.Select(entry => entry.Message.Text));
        await fixture.Service.ExecuteAsync(plan);
        Assert.Equal(
            "cccc",
            Assert.Single(await fixture.Store.GetHistoryAsync(fixture.Session.Id)).Text);
    }

    [Fact]
    public async Task ExecuteAsync_PassesPreviousSummaryIntoNextRollingCompaction()
    {
        var generator = new RecordingSummaryGenerator(DefaultSummary());
        var fixture = CreateFixture(generator: generator);
        await AddMessagesAsync(fixture, "one", "two", "three", "four");
        await fixture.Service.ExecuteAsync(
            (await fixture.Service.PrepareAsync(fixture.Session.Id))!);
        await AddMessagesAsync(fixture, "five", "six");

        await fixture.Service.ExecuteAsync(
            (await fixture.Service.PrepareAsync(fixture.Session.Id))!);

        Assert.Equal(2, generator.PreviousSummaries.Count);
        Assert.Null(generator.PreviousSummaries[0]);
        Assert.Equal(DefaultSummary(), generator.PreviousSummaries[1]);
        Assert.Equal(2, (await fixture.Store.GetSummaryAsync(fixture.Session.Id))!.Version);
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotDeleteMessagesWhenGenerationFails()
    {
        var fixture = CreateFixture(generator: new ThrowingSummaryGenerator());
        await AddMessagesAsync(fixture, "one", "two", "three", "four");
        var plan = await fixture.Service.PrepareAsync(fixture.Session.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.ExecuteAsync(plan!));

        Assert.Equal(4, (await fixture.Store.GetHistoryAsync(fixture.Session.Id)).Count);
        Assert.Null(await fixture.Store.GetSummaryAsync(fixture.Session.Id));
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotDeleteMessagesWhenSummaryExceedsLimit()
    {
        var oversized = new ConversationSummaryContent(
            new string('x', 101),
            "",
            "",
            "");
        var fixture = CreateFixture(generator: new RecordingSummaryGenerator(oversized));
        await AddMessagesAsync(fixture, "one", "two", "three", "four");
        var plan = await fixture.Service.PrepareAsync(fixture.Session.Id);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Service.ExecuteAsync(plan!));

        Assert.Equal(4, (await fixture.Store.GetHistoryAsync(fixture.Session.Id)).Count);
        Assert.Null(await fixture.Store.GetSummaryAsync(fixture.Session.Id));
    }

    [Fact]
    public async Task PrepareAsync_ReturnsNullBelowConfiguredThresholds()
    {
        var fixture = CreateFixture();
        await AddMessagesAsync(fixture, "one", "two", "three");

        Assert.Null(await fixture.Service.PrepareAsync(fixture.Session.Id));
    }

    private static CompactionFixture CreateFixture(
        ConversationCompactionOptions? options = null,
        IConversationSummaryGenerator? generator = null)
    {
        var participantA = new Participant(Guid.NewGuid(), "A");
        var participantB = new Participant(Guid.NewGuid(), "B");
        var session = new Session(Guid.NewGuid(), participantA, participantB);
        var store = new InMemoryConversationStore([session]);
        var effectiveOptions = options ?? new ConversationCompactionOptions
        {
            Enabled = true,
            TriggerMessageCount = 4,
            TriggerHistoryCharacters = 10_000,
            RetainRecentMessageCount = 2,
            RetainRecentCharacters = 5_000,
            MaxSummaryCharacters = 100,
            MaxOutputTokens = 100,
            OperationTimeout = TimeSpan.FromSeconds(5),
            RetryDelay = TimeSpan.FromSeconds(1)
        };
        var effectiveGenerator = generator ?? new RecordingSummaryGenerator(DefaultSummary());
        return new CompactionFixture(
            session,
            store,
            new ConversationCompactionService(
                store,
                store,
                effectiveGenerator,
                effectiveOptions));
    }

    private static async Task AddMessagesAsync(
        CompactionFixture fixture,
        params string[] texts)
    {
        foreach (var text in texts)
        {
            await fixture.Store.SaveMessageAsync(new Message(
                Guid.NewGuid(),
                fixture.Session.Id,
                fixture.Session.ParticipantA.Id,
                null,
                MessageDirection.ParticipantToMediator,
                text,
                DateTimeOffset.UtcNow));
        }
    }

    private static ConversationSummaryContent DefaultSummary() => new(
        "private-a",
        "private-b",
        "shared",
        "safety");

    private sealed record CompactionFixture(
        Session Session,
        InMemoryConversationStore Store,
        ConversationCompactionService Service);

    private sealed class RecordingSummaryGenerator(ConversationSummaryContent result)
        : IConversationSummaryGenerator
    {
        public List<ConversationSummaryContent?> PreviousSummaries { get; } = [];

        public Task<ConversationSummaryContent> GenerateAsync(
            Session session,
            ConversationSummaryContent? previousSummary,
            IReadOnlyList<SequencedMessage> messages,
            int maxSummaryCharacters,
            CancellationToken cancellationToken = default)
        {
            PreviousSummaries.Add(previousSummary);
            return Task.FromResult(result);
        }
    }

    private sealed class ThrowingSummaryGenerator : IConversationSummaryGenerator
    {
        public Task<ConversationSummaryContent> GenerateAsync(
            Session session,
            ConversationSummaryContent? previousSummary,
            IReadOnlyList<SequencedMessage> messages,
            int maxSummaryCharacters,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Simulated compaction failure.");
    }
}
