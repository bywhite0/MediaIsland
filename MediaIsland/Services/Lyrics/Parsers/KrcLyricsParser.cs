using System.Globalization;
using MediaIsland.Services.Lyrics.Models;

namespace MediaIsland.Services.Lyrics.Parsers;

/// <summary>
/// Parses Kugou KRC lyrics into word-synced <see cref="LyricsLine"/> values.
/// </summary>
/// <remarks>
/// A timed line looks like <c>[lineStart,lineDuration]&lt;offset,duration,0&gt;word...</c>, where each
/// word offset is relative to the line start. Metadata lines such as <c>[ar:...]</c> carry no timing
/// and are skipped.
/// </remarks>
public static class KrcLyricsParser
{
    public static IReadOnlyList<LyricsLine> Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        var lines = new List<LyricsLine>();
        foreach (var rawLine in content.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryParseLine(rawLine, out var line))
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    private static bool TryParseLine(string rawLine, out LyricsLine line)
    {
        line = null!;
        if (rawLine.Length < 5 || rawLine[0] != '[')
        {
            return false;
        }

        var comma = rawLine.IndexOf(',', 1);
        if (comma <= 1)
        {
            return false;
        }

        var closeBracket = rawLine.IndexOf(']', comma + 1);
        if (closeBracket <= comma + 1 ||
            !TryParseMilliseconds(rawLine.AsSpan(1, comma - 1), out var lineStart) ||
            !TryParseMilliseconds(rawLine.AsSpan(comma + 1, closeBracket - comma - 1), out _))
        {
            return false;
        }

        var words = ParseWords(rawLine.AsSpan(closeBracket + 1), lineStart);
        if (words.Count == 0)
        {
            return false;
        }

        line = new LyricsLine(
            words[0].StartTime,
            words[^1].EndTime,
            string.Concat(words.Select(word => word.Text)),
            words);
        return true;
    }

    private static IReadOnlyList<LyricsWord> ParseWords(ReadOnlySpan<char> content, int lineStartMilliseconds)
    {
        var words = new List<LyricsWord>();
        var index = 0;
        while (index < content.Length)
        {
            var openBracket = content[index..].IndexOf('<');
            if (openBracket < 0)
            {
                break;
            }

            openBracket += index;
            var closeBracket = content[openBracket..].IndexOf('>');
            if (closeBracket < 0)
            {
                break;
            }

            closeBracket += openBracket;
            if (!TryParseTiming(content[(openBracket + 1)..closeBracket], out var offset, out var duration))
            {
                index = openBracket + 1;
                continue;
            }

            var textStart = closeBracket + 1;
            var nextBracket = content[textStart..].IndexOf('<');
            var textEnd = nextBracket < 0 ? content.Length : textStart + nextBracket;

            var start = TimeSpan.FromMilliseconds(lineStartMilliseconds + offset);
            words.Add(new LyricsWord(
                start,
                start.Add(TimeSpan.FromMilliseconds(duration)),
                content[textStart..textEnd].ToString()));
            index = textEnd;
        }

        return words;
    }

    private static bool TryParseTiming(ReadOnlySpan<char> timing, out int offset, out int duration)
    {
        offset = 0;
        duration = 0;
        var firstComma = timing.IndexOf(',');
        if (firstComma <= 0 || !TryParseMilliseconds(timing[..firstComma], out offset))
        {
            return false;
        }

        var rest = timing[(firstComma + 1)..];
        var secondComma = rest.IndexOf(',');
        return TryParseMilliseconds(secondComma < 0 ? rest : rest[..secondComma], out duration);
    }

    private static bool TryParseMilliseconds(ReadOnlySpan<char> value, out int milliseconds) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out milliseconds);
}
