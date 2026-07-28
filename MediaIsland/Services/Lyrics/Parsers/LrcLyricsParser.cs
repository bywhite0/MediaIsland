using System.Globalization;
using MediaIsland.Services.Lyrics.Models;

namespace MediaIsland.Services.Lyrics.Parsers;

/// <summary>
/// Parses LRC lyrics into line-synced <see cref="LyricsLine"/> values.
/// </summary>
/// <remarks>
/// Supported tag forms are <c>[mm:ss]</c>, <c>[mm:ss.fff]</c> and <c>[mm:ss:fff]</c>. Several
/// timestamps may share one lyric line, and an <c>[offset:n]</c> attribute shifts every following
/// line by <c>-n</c> milliseconds, matching the LRC convention where a positive offset makes
/// lyrics appear earlier.
/// </remarks>
public static class LrcLyricsParser
{
    public static IReadOnlyList<LyricsLine> Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        var entries = new List<(TimeSpan Start, string Text)>();
        var timestamps = new List<TimeSpan>();
        var offset = TimeSpan.Zero;

        foreach (var rawLine in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            timestamps.Clear();
            var index = ReadLeadingTags(rawLine, timestamps, ref offset);
            if (timestamps.Count == 0)
            {
                continue;
            }

            var text = rawLine[index..].Trim();
            foreach (var timestamp in timestamps)
            {
                entries.Add((timestamp - offset, text));
            }
        }

        return entries
            .OrderBy(entry => entry.Start)
            .Select(entry => new LyricsLine(entry.Start, TimeSpan.Zero, entry.Text, []))
            .ToArray();
    }

    /// <summary>Consumes the tag prefix of a line and returns the index where the lyric text starts.</summary>
    private static int ReadLeadingTags(string line, List<TimeSpan> timestamps, ref TimeSpan offset)
    {
        var index = 0;
        while (index < line.Length && line[index] == '[')
        {
            var close = line.IndexOf(']', index + 1);
            if (close < 0)
            {
                break;
            }

            var tag = line.AsSpan(index + 1, close - index - 1);
            if (TryParseTimestamp(tag, out var timestamp))
            {
                timestamps.Add(timestamp);
                index = close + 1;
                continue;
            }

            // Once a timestamp has been seen, any other bracketed tag is part of the lyric text.
            if (timestamps.Count > 0)
            {
                break;
            }

            ReadAttribute(tag, ref offset);
            index = close + 1;
        }

        return index;
    }

    private static void ReadAttribute(ReadOnlySpan<char> tag, ref TimeSpan offset)
    {
        var separator = tag.IndexOf(':');
        if (separator <= 0 ||
            !tag[..separator].Trim().Equals("offset", StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(tag[(separator + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds))
        {
            return;
        }

        offset = TimeSpan.FromMilliseconds(milliseconds);
    }

    private static bool TryParseTimestamp(ReadOnlySpan<char> tag, out TimeSpan timestamp)
    {
        timestamp = TimeSpan.Zero;
        var minuteEnd = tag.IndexOf(':');
        if (minuteEnd <= 0 || !TryParseDigits(tag[..minuteEnd], out var minutes))
        {
            return false;
        }

        var rest = tag[(minuteEnd + 1)..];
        var secondEnd = rest.IndexOfAny('.', ':');
        var seconds = secondEnd < 0 ? rest : rest[..secondEnd];
        if (!TryParseDigits(seconds, out var secondValue))
        {
            return false;
        }

        var milliseconds = 0;
        if (secondEnd >= 0 && !TryParseFraction(rest[(secondEnd + 1)..], out milliseconds))
        {
            return false;
        }

        timestamp = TimeSpan.FromMilliseconds((minutes * 60L + secondValue) * 1000L + milliseconds);
        return true;
    }

    private static bool TryParseDigits(ReadOnlySpan<char> value, out int result) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result);

    /// <summary>Reads a fractional second as milliseconds, so <c>.5</c>, <c>.50</c> and <c>.500</c> all mean 500 ms.</summary>
    private static bool TryParseFraction(ReadOnlySpan<char> value, out int milliseconds)
    {
        milliseconds = 0;
        if (value.IsEmpty)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }

        for (var i = 0; i < 3; i++)
        {
            milliseconds = milliseconds * 10 + (i < value.Length ? value[i] - '0' : 0);
        }

        return true;
    }
}
