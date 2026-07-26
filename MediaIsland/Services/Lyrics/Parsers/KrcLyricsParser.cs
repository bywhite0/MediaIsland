using System.Globalization;
using System.Text.Json;
using MediaIsland.Services.Lyrics.Models;

namespace MediaIsland.Services.Lyrics.Parsers;

/// <summary>
/// Parses Kugou KRC lyrics into word-synced <see cref="LyricsLine"/> values.
/// </summary>
/// <remarks>
/// A timed line looks like <c>[lineStart,lineDuration]&lt;offset,duration,0&gt;word...</c>, where each
/// word offset is relative to the line start. Metadata lines such as <c>[ar:...]</c> carry no timing
/// and are skipped, except <c>[language:...]</c> which carries the per-line translation.
/// </remarks>
public static class KrcLyricsParser
{
    /// <summary>Tag holding the base64-encoded translation document.</summary>
    private const string LanguageTag = "[language:";

    /// <summary>Content block type carrying a plain translation; other types are alternate renderings.</summary>
    private const int TranslationContentType = 1;

    /// <summary>Kugou marks a line with no translation as "//".</summary>
    private const string EmptyTranslationMarker = "//";

    public static IReadOnlyList<LyricsLine> Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        var lines = new List<LyricsLine>();
        // Translations are indexed by timed line, so keep that index even for lines that yield no words.
        var timedLineIndexes = new List<int>();
        var timedLineCount = 0;

        foreach (var rawLine in content.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryParseLineHeader(rawLine, out var lineStart, out var wordsStart))
            {
                continue;
            }

            var timedLineIndex = timedLineCount++;
            var words = ParseWords(rawLine.AsSpan(wordsStart), lineStart);
            if (words.Count == 0)
            {
                continue;
            }

            lines.Add(new LyricsLine(
                words[0].StartTime,
                words[^1].EndTime,
                string.Concat(words.Select(word => word.Text)),
                words));
            timedLineIndexes.Add(timedLineIndex);
        }

        AttachTranslations(lines, timedLineIndexes, content);
        return lines;
    }

    private static bool TryParseLineHeader(string rawLine, out int lineStart, out int wordsStart)
    {
        lineStart = 0;
        wordsStart = 0;
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
            !TryParseMilliseconds(rawLine.AsSpan(1, comma - 1), out lineStart) ||
            !TryParseMilliseconds(rawLine.AsSpan(comma + 1, closeBracket - comma - 1), out _))
        {
            return false;
        }

        wordsStart = closeBracket + 1;
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

    private static void AttachTranslations(List<LyricsLine> lines, List<int> timedLineIndexes, string content)
    {
        var translations = ReadTranslations(content);
        if (translations == null)
        {
            return;
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var timedLineIndex = timedLineIndexes[i];
            if (timedLineIndex >= translations.Count)
            {
                break;
            }

            var translation = translations[timedLineIndex];
            if (!string.IsNullOrWhiteSpace(translation))
            {
                lines[i] = lines[i] with { Translation = translation };
            }
        }
    }

    /// <summary>
    /// Reads the translation lines from the <c>[language:...]</c> tag, which holds base64-encoded
    /// JSON of the form <c>{"content":[{"type":1,"lyricContent":[["line"],...]}]}</c>.
    /// </summary>
    private static IReadOnlyList<string>? ReadTranslations(string content)
    {
        var start = content.IndexOf(LanguageTag, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += LanguageTag.Length;
        var end = content.IndexOf(']', start);
        if (end <= start)
        {
            return null;
        }

        try
        {
            var json = Convert.FromBase64String(content[start..end].Trim());
            using var document = JsonDocument.Parse(json);
            return ReadTranslationDocument(document.RootElement);
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string>? ReadTranslationDocument(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("content", out var blocks) ||
            blocks.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var block in blocks.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object ||
                !block.TryGetProperty("type", out var type) ||
                type.ValueKind != JsonValueKind.Number ||
                !type.TryGetInt32(out var typeValue) ||
                typeValue != TranslationContentType ||
                !block.TryGetProperty("lyricContent", out var rows) ||
                rows.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var translations = rows.EnumerateArray().Select(ReadTranslationRow).ToArray();
            if (translations.Length > 0)
            {
                return translations;
            }
        }

        return null;
    }

    private static string ReadTranslationRow(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Array ||
            row.GetArrayLength() == 0 ||
            row[0].ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        var text = row[0].GetString() ?? string.Empty;
        return text == EmptyTranslationMarker ? string.Empty : text;
    }
}
