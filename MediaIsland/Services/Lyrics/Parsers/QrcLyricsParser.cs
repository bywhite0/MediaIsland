using System.Globalization;
using MediaIsland.Services.Lyrics.Models;

namespace MediaIsland.Services.Lyrics.Parsers;

/// <summary>
/// Parses QQ Music QRC lyrics into word-synced <see cref="LyricsLine"/> values.
/// </summary>
/// <remarks>
/// A timed line looks like <c>[lineStart,lineDuration]word(start,duration)...</c>, where each word
/// timestamp is absolute. Metadata lines such as <c>[ti:...]</c> carry no timing and are skipped.
/// </remarks>
public static class QrcLyricsParser
{
    public static IReadOnlyList<LyricsLine> Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        var lines = new List<LyricsLine>();
        string? kanaPayload = null;
        // QQ Music sometimes returns line breaks escaped as literal "\n" inside a single JSON string.
        foreach (var rawLine in content.Split(
                     ["\r\n", "\n", "\r", "\\r\\n", "\\n", "\\r"],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kana = LyricsKanaRubyParser.ExtractPayload(rawLine);
            if (kana != null)
            {
                // Real QQ tracks ship a single whole-song stream; keep the first non-empty payload.
                if (string.IsNullOrEmpty(kanaPayload) && kana.Length > 0)
                {
                    kanaPayload = kana;
                }

                continue;
            }

            if (!TryParseLineHeader(rawLine, out var startMilliseconds, out var durationMilliseconds, out var contentText))
            {
                continue;
            }

            var words = ParseWords(contentText);
            var lineStart = TimeSpan.FromMilliseconds(startMilliseconds);
            var text = words.Count > 0
                ? string.Concat(words.Select(word => word.Text))
                : contentText.Trim();
            lines.Add(new LyricsLine(
                lineStart,
                lineStart.Add(TimeSpan.FromMilliseconds(durationMilliseconds)),
                text,
                words));
        }

        return LyricsKanaRubyParser.Attach(lines, kanaPayload);
    }

    private static bool TryParseLineHeader(
        string line,
        out int startMilliseconds,
        out int durationMilliseconds,
        out string content)
    {
        startMilliseconds = 0;
        durationMilliseconds = 0;
        content = string.Empty;
        if (line.Length < 5 || line[0] != '[')
        {
            return false;
        }

        var comma = line.IndexOf(',', 1);
        if (comma <= 1)
        {
            return false;
        }

        var closeBracket = line.IndexOf(']', comma + 1);
        if (closeBracket <= comma + 1 ||
            !TryParseMilliseconds(line.AsSpan(1, comma - 1), out startMilliseconds) ||
            !TryParseMilliseconds(line.AsSpan(comma + 1, closeBracket - comma - 1), out durationMilliseconds))
        {
            return false;
        }

        content = line[(closeBracket + 1)..];
        return true;
    }

    private static IReadOnlyList<LyricsWord> ParseWords(string content)
    {
        var words = new List<LyricsWord>();
        var wordTextStart = 0;
        var searchStart = 0;
        while (searchStart < content.Length)
        {
            var openParenthesis = content.IndexOf('(', searchStart);
            if (openParenthesis < 0)
            {
                break;
            }

            var comma = content.IndexOf(',', openParenthesis + 1);
            var closeParenthesis = comma >= 0 ? content.IndexOf(')', comma + 1) : -1;
            if (comma <= openParenthesis + 1 ||
                closeParenthesis <= comma + 1 ||
                !TryParseMilliseconds(content.AsSpan(openParenthesis + 1, comma - openParenthesis - 1), out var startMilliseconds) ||
                !TryParseMilliseconds(content.AsSpan(comma + 1, closeParenthesis - comma - 1), out var durationMilliseconds))
            {
                searchStart = openParenthesis + 1;
                continue;
            }

            var wordStart = TimeSpan.FromMilliseconds(startMilliseconds);
            words.Add(new LyricsWord(
                wordStart,
                wordStart.Add(TimeSpan.FromMilliseconds(durationMilliseconds)),
                content[wordTextStart..openParenthesis]));
            wordTextStart = closeParenthesis + 1;
            searchStart = wordTextStart;
        }

        return words;
    }

    private static bool TryParseMilliseconds(ReadOnlySpan<char> value, out int milliseconds) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out milliseconds);
}
