using System.Globalization;
using System.Text;
using MediaIsland.Services.Lyrics.Models;

namespace MediaIsland.Services.Lyrics.Parsers;

/// <summary>
/// Parses QQ Music QRC <c>[kana:...]</c> metadata into per-line ruby spans.
/// </summary>
/// <remarks>
/// The payload is a single stream for the whole lyric track: each token is a one-digit
/// cover count (0-9) followed by a reading. Cover counts only CJK ideographs in document
/// order; kana and ASCII in the lyric text are skipped. Optional <c>(start,duration)</c>
/// fragments inside a reading are stripped and ignored for display.
/// </remarks>
internal static class QrcKanaParser
{
    public static string? ExtractPayload(string line)
    {
        if (line.Length < 7 ||
            line[0] != '[' ||
            !line.StartsWith("[kana:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var close = line.LastIndexOf(']');
        if (close <= 6)
        {
            return null;
        }

        return line[6..close];
    }

    public static IReadOnlyList<LyricsLine> Attach(
        IReadOnlyList<LyricsLine> lines,
        string? kanaPayload)
    {
        if (lines.Count == 0 || string.IsNullOrEmpty(kanaPayload))
        {
            return lines;
        }

        var tokens = ParseTokens(kanaPayload);
        if (tokens.Count == 0)
        {
            return lines;
        }

        var positions = new List<(int LineIndex, int CharIndex)>();
        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var text = lines[lineIndex].Text ?? string.Empty;
            for (var charIndex = 0; charIndex < text.Length; charIndex++)
            {
                if (IsAlignableIdeograph(text[charIndex]))
                {
                    positions.Add((lineIndex, charIndex));
                }
            }
        }

        if (positions.Count == 0)
        {
            return lines;
        }

        var spansByLine = new List<LyricsRubySpan>?[lines.Count];
        var posCursor = 0;
        foreach (var token in tokens)
        {
            if (token.Cover <= 0)
            {
                continue;
            }

            if (posCursor + token.Cover > positions.Count)
            {
                break;
            }

            var start = positions[posCursor];
            var end = positions[posCursor + token.Cover - 1];
            posCursor += token.Cover;

            if (start.LineIndex != end.LineIndex)
            {
                // Multi-line spans are not representable on a single LyricsLine.
                continue;
            }

            if (string.IsNullOrEmpty(token.Reading))
            {
                continue;
            }

            var baseStart = start.CharIndex;
            var baseLength = end.CharIndex - start.CharIndex + 1;
            if (baseLength <= 0)
            {
                continue;
            }

            spansByLine[start.LineIndex] ??= [];
            spansByLine[start.LineIndex]!.Add(new LyricsRubySpan(baseStart, baseLength, token.Reading));
        }

        if (spansByLine.All(static spans => spans is not { Count: > 0 }))
        {
            return lines;
        }

        var result = lines.ToArray();
        for (var i = 0; i < result.Length; i++)
        {
            var spans = spansByLine[i];
            if (spans is { Count: > 0 })
            {
                result[i] = result[i] with { RubySpans = spans };
            }
        }

        return result;
    }

    internal static IReadOnlyList<KanaToken> ParseTokens(string payload)
    {
        var tokens = new List<KanaToken>();
        var i = 0;
        while (i < payload.Length)
        {
            var ch = payload[i];
            if (ch is < '0' or > '9')
            {
                i++;
                continue;
            }

            var cover = ch - '0';
            i++;
            var reading = ReadReading(payload, ref i);
            tokens.Add(new KanaToken(cover, reading));
        }

        return tokens;
    }

    private static string ReadReading(string payload, ref int index)
    {
        var builder = new StringBuilder();
        while (index < payload.Length)
        {
            var ch = payload[index];
            if (ch is >= '0' and <= '9')
            {
                break;
            }

            if (ch == '(')
            {
                var close = payload.IndexOf(')', index + 1);
                if (close > index && LooksLikeTiming(payload, index + 1, close))
                {
                    index = close + 1;
                    continue;
                }
            }

            builder.Append(ch);
            index++;
        }

        return builder.ToString();
    }

    private static bool LooksLikeTiming(string payload, int start, int close)
    {
        var comma = payload.IndexOf(',', start, close - start);
        if (comma <= start)
        {
            return false;
        }

        return int.TryParse(payload.AsSpan(start, comma - start), NumberStyles.None, CultureInfo.InvariantCulture, out _) &&
               int.TryParse(payload.AsSpan(comma + 1, close - comma - 1), NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }

    internal static bool IsAlignableIdeograph(char value)
    {
        // CJK Unified Ideographs + Extension A. Kana and ASCII are intentionally excluded.
        return value is (>= '一' and <= '鿿') or (>= '㐀' and <= '䶿');
    }

    internal readonly record struct KanaToken(int Cover, string Reading);
}
