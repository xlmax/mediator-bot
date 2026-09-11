using System.Globalization;

namespace MediatorBot.Telegram;

public sealed class TelegramTextChunker
{
    public const int DefaultMaxChunkLength = 4000;

    private readonly int _maxChunkLength;

    public TelegramTextChunker(int maxChunkLength = DefaultMaxChunkLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxChunkLength);
        _maxChunkLength = maxChunkLength;
    }

    public IReadOnlyList<string> Split(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (text.Length <= _maxChunkLength)
        {
            return [text];
        }

        var textElementStarts = StringInfo.ParseCombiningCharacters(text);
        var chunks = new List<string>();
        var start = 0;
        while (text.Length - start > _maxChunkLength)
        {
            var hardEnd = FindTextElementBoundary(
                textElementStarts,
                start + _maxChunkLength);
            if (hardEnd <= start)
            {
                throw new InvalidOperationException(
                    "A single Unicode text element exceeds the Telegram chunk limit.");
            }

            var preferredEnd = FindPreferredBoundary(text, start, hardEnd);
            var end = preferredEnd > start ? preferredEnd : hardEnd;
            chunks.Add(text[start..end]);
            start = end;
        }

        if (start < text.Length)
        {
            chunks.Add(text[start..]);
        }

        return chunks;
    }

    private static int FindTextElementBoundary(
        int[] textElementStarts,
        int maximumEnd)
    {
        var index = Array.BinarySearch(textElementStarts, maximumEnd);
        if (index >= 0)
        {
            return maximumEnd;
        }

        var insertionIndex = ~index;
        return insertionIndex == 0
            ? 0
            : textElementStarts[insertionIndex - 1];
    }

    private static int FindPreferredBoundary(
        string text,
        int start,
        int hardEnd)
    {
        var minimumPreferredEnd = start + ((hardEnd - start) / 2);
        var paragraphEnd = FindLastDelimiterEnd(
            text,
            start,
            hardEnd,
            "\r\n\r\n");
        if (paragraphEnd >= minimumPreferredEnd)
        {
            return paragraphEnd;
        }

        paragraphEnd = FindLastDelimiterEnd(text, start, hardEnd, "\n\n");
        if (paragraphEnd >= minimumPreferredEnd)
        {
            return paragraphEnd;
        }

        for (var index = hardEnd - 1; index >= minimumPreferredEnd; index--)
        {
            if (text[index] == '\n')
            {
                return index + 1;
            }
        }

        for (var index = hardEnd - 1; index >= minimumPreferredEnd; index--)
        {
            if (char.IsWhiteSpace(text[index]) &&
                index > start &&
                IsSentenceTerminator(text[index - 1]))
            {
                return index + 1;
            }
        }

        for (var index = hardEnd - 1; index >= minimumPreferredEnd; index--)
        {
            if (char.IsWhiteSpace(text[index]))
            {
                return index + 1;
            }
        }

        return hardEnd;
    }

    private static int FindLastDelimiterEnd(
        string text,
        int start,
        int end,
        string delimiter)
    {
        var relativeIndex = text.AsSpan(start, end - start).LastIndexOf(delimiter);
        return relativeIndex < 0
            ? -1
            : start + relativeIndex + delimiter.Length;
    }

    private static bool IsSentenceTerminator(char character) =>
        character is '.' or '!' or '?' or '…' or ':' or ';';
}
