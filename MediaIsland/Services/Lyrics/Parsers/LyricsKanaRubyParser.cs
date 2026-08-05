using System.Globalization;
using System.Text;
using MediaIsland.Services.Lyrics.Models;

namespace MediaIsland.Services.Lyrics.Parsers;

/// <summary>
/// Parses whole-track <c>[kana:...]</c> metadata (QRC/KRC) into per-line ruby spans.
/// </summary>
/// <remarks>
/// The payload is a single stream for the whole lyric track: each token is a one-digit
/// cover count (0-9) followed by a reading. Optional <c>(start,duration)</c> fragments inside
/// a reading are stripped and ignored for display.
/// <para>
/// The stream carries no anchors, so it only lines up if our notion of "base character" matches
/// the producer's exactly — a single extra or missing base character shifts every later reading.
/// Verified against real QQ Music payloads: a base character is a CJK ideograph, or a maximal run
/// of digits (<c>27</c> and <c>0</c> each count as one). Kana and Latin are never base characters.
/// A base character the producer could not read still consumes a slot with an empty reading, so
/// "unannotated" never means "unconsumed".
/// </para>
/// <para>
/// Because every base character is covered exactly once, the total cover count is a checksum:
/// it must equal the number of base characters. That lets us both pick the right base-character
/// definition per track and refuse to render anything when none of them fits.
/// </para>
/// </remarks>
internal static class LyricsKanaRubyParser
{
    /// <summary>
    /// Base-character definitions to try, most likely first. The cover checksum decides which one
    /// the producer used for a given track.
    /// </summary>
    private static readonly (bool DigitRuns, bool IterationMarks)[] BaseCandidates =
    [
        (true, false),
        (false, false),
        (true, true),
        (false, true)
    ];

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

        var totalCover = tokens.Sum(static token => token.Cover > 0 ? token.Cover : 0);
        var positions = ResolvePositions(lines, totalCover);
        if (positions == null)
        {
            // 无法判定生成侧的基字符口径，任何分配都会整体错位；宁可不显示注音。
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
            var baseLength = end.CharIndex + end.CharLength - start.CharIndex;
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

    /// <summary>
    /// Picks the base-character definition whose count matches <paramref name="totalCover"/>,
    /// or <c>null</c> when no candidate balances.
    /// </summary>
    private static IReadOnlyList<BasePosition>? ResolvePositions(
        IReadOnlyList<LyricsLine> lines,
        int totalCover)
    {
        if (totalCover <= 0)
        {
            return null;
        }

        foreach (var (digitRuns, iterationMarks) in BaseCandidates)
        {
            var positions = BuildPositions(lines, digitRuns, iterationMarks);
            if (positions.Count == totalCover)
            {
                return positions;
            }
        }

        return null;
    }

    private static List<BasePosition> BuildPositions(
        IReadOnlyList<LyricsLine> lines,
        bool digitRuns,
        bool iterationMarks)
    {
        var positions = new List<BasePosition>();
        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var text = lines[lineIndex].Text ?? string.Empty;
            var charIndex = 0;
            while (charIndex < text.Length)
            {
                var value = text[charIndex];
                if (IsAlignableIdeograph(value) || (iterationMarks && IsIterationMark(value)))
                {
                    positions.Add(new BasePosition(lineIndex, charIndex, 1));
                    charIndex++;
                    continue;
                }

                if (digitRuns && IsDigit(value))
                {
                    var runStart = charIndex;
                    while (charIndex < text.Length && IsDigit(text[charIndex]))
                    {
                        charIndex++;
                    }

                    positions.Add(new BasePosition(lineIndex, runStart, charIndex - runStart));
                    continue;
                }

                charIndex++;
            }
        }

        return positions;
    }

    internal static bool IsAlignableIdeograph(char value)
    {
        // CJK Unified Ideographs + Extension A. Kana and ASCII are intentionally excluded.
        return value is (>= '一' and <= '鿿') or (>= '㐀' and <= '䶿');
    }

    /// <summary>Half-width and full-width digits; a maximal run counts as one base character.</summary>
    private static bool IsDigit(char value) => value is (>= '0' and <= '9') or (>= '０' and <= '９');

    /// <summary>Iteration/abbreviation marks that live outside the ideograph blocks.</summary>
    private static bool IsIterationMark(char value) => value is '々' or '〇' or '〆';

    /// <param name="CharLength">1 for an ideograph; the run length for a digit run.</param>
    private readonly record struct BasePosition(int LineIndex, int CharIndex, int CharLength);

    internal readonly record struct KanaToken(int Cover, string Reading);
}
