using MediatorBot.Telegram;

namespace MediatorBot.Tests;

public sealed class TelegramTextChunkerTests
{
    private readonly TelegramTextChunker _chunker = new();

    [Fact]
    public void Split_ReturnsShortTextUnchanged()
    {
        const string text = "Короткий ответ";

        var chunks = _chunker.Split(text);

        Assert.Equal(text, Assert.Single(chunks));
    }

    [Fact]
    public void Split_PreservesAllTextAndPrefersParagraphBoundary()
    {
        var text = new string('а', 3800) + "\n\n" + new string('б', 500);

        var chunks = _chunker.Split(text);

        Assert.Equal(2, chunks.Count);
        Assert.EndsWith("\n\n", chunks[0]);
        Assert.Equal(text, string.Concat(chunks));
        Assert.All(
            chunks,
            chunk => Assert.InRange(
                chunk.Length,
                1,
                TelegramTextChunker.DefaultMaxChunkLength));
    }

    [Fact]
    public void Split_DoesNotBreakUnicodeTextElements()
    {
        var text = string.Concat(Enumerable.Repeat("текст 👩‍💻 ", 1000));

        var chunks = _chunker.Split(text);

        Assert.True(chunks.Count > 1);
        Assert.Equal(text, string.Concat(chunks));
        Assert.All(chunks, chunk =>
        {
            Assert.InRange(
                chunk.Length,
                1,
                TelegramTextChunker.DefaultMaxChunkLength);
            Assert.False(char.IsLowSurrogate(chunk[0]));
            Assert.False(char.IsHighSurrogate(chunk[^1]));
            Assert.NotEqual('\u200d', chunk[0]);
            Assert.NotEqual('\u200d', chunk[^1]);
        });
    }

    [Fact]
    public void Split_UsesHardUnicodeBoundariesWhenThereIsNoWhitespace()
    {
        var text = new string('я', 8500);

        var chunks = _chunker.Split(text);

        Assert.Equal([4000, 4000, 500], chunks.Select(chunk => chunk.Length));
        Assert.Equal(text, string.Concat(chunks));
    }
}
