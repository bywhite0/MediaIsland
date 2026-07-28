using System.Globalization;
using System.Text;
using System.Text.Json;
using MediaIsland.Services.Lyrics.Models;

namespace MediaIsland.Services.Lyrics.Parsers;

/// <summary>
/// Parses Kugou KRC lyrics into word-synced <see cref="LyricsLine"/> values.
/// </summary>
/// <remarks>
/// A timed line looks like <c>[lineStart,lineDuration]&lt;offset,duration,0&gt;word...</c>, where each
/// word offset is relative to the line start. Metadata lines such as <c>[ar:...]</c> carry no timing
/// and are skipped, except <c>[language:...]</c> (translation/romanization) and
/// <c>[kana:...]</c> (whole-track ruby readings shared with QRC).
/// </remarks>
public static class KrcLyricsParser
{
    /// <summary>Tag holding the base64-encoded translation document.</summary>
    private const string LanguageTag = "[language:";

    /// <summary>Content block holding one romanization cell per KRC syllable.</summary>
    private const int RomanizationContentType = 0;

    /// <summary>Content block holding one translation string per line.</summary>
    private const int TranslationContentType = 1;

    /// <summary>Kugou marks a line with no translation as "//".</summary>
    private const string EmptyTextMarker = "//";

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
        string? kanaPayload = null;

        foreach (var rawLine in content.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kana = LyricsKanaRubyParser.ExtractPayload(rawLine);
            if (kana != null)
            {
                // Whole-track stream; keep the first non-empty payload (same as QRC).
                if (string.IsNullOrEmpty(kanaPayload) && kana.Length > 0)
                {
                    kanaPayload = kana;
                }

                continue;
            }

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

        AttachSecondaryText(lines, timedLineIndexes, content);
        return LyricsKanaRubyParser.Attach(lines, kanaPayload);
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

    private static void AttachSecondaryText(List<LyricsLine> lines, List<int> timedLineIndexes, string content)
    {
        if (lines.Count == 0 || !TryReadLanguageJson(content, out var json))
        {
            return;
        }

        IReadOnlyList<string>? translations;
        IReadOnlyList<string>? romanizations;
        try
        {
            using var document = JsonDocument.Parse(json);
            translations = ReadContentBlock(document.RootElement, TranslationContentType, ReadFirstCell);
            romanizations = ReadContentBlock(document.RootElement, RomanizationContentType, JoinSyllableCells);
        }
        catch (JsonException)
        {
            return;
        }

        Apply(lines, timedLineIndexes, translations, static (line, text) => line with { Translation = text });
        Apply(lines, timedLineIndexes, romanizations, static (line, text) => line with { Romanization = text });
    }

    private static void Apply(
        List<LyricsLine> lines,
        List<int> timedLineIndexes,
        IReadOnlyList<string>? values,
        Func<LyricsLine, string, LyricsLine> attach)
    {
        if (values == null)
        {
            return;
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var timedLineIndex = timedLineIndexes[i];
            if (timedLineIndex >= values.Count)
            {
                break;
            }

            var value = values[timedLineIndex];
            if (!string.IsNullOrWhiteSpace(value))
            {
                lines[i] = attach(lines[i], value);
            }
        }
    }

    /// <summary>
    /// Extracts the base64 payload of the <c>[language:...]</c> tag, which decodes to JSON of the form
    /// <c>{"content":[{"type":1,"lyricContent":[["line"],...]}]}</c>.
    /// </summary>
    private static bool TryReadLanguageJson(string content, out byte[] json)
    {
        json = [];
        var start = content.IndexOf(LanguageTag, StringComparison.Ordinal);
        if (start < 0)
        {
            return false;
        }

        start += LanguageTag.Length;
        var end = content.IndexOf(']', start);
        if (end <= start)
        {
            return false;
        }

        try
        {
            json = Convert.FromBase64String(content[start..end].Trim());
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static IReadOnlyList<string>? ReadContentBlock(
        JsonElement root,
        int contentType,
        Func<JsonElement, string> readRow)
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
                typeValue != contentType ||
                !block.TryGetProperty("lyricContent", out var rows) ||
                rows.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var values = rows.EnumerateArray().Select(readRow).ToArray();
            if (values.Length > 0)
            {
                return values;
            }
        }

        return null;
    }

    /// <summary>Reads a translation row, which holds the whole line in its first cell.</summary>
    private static string ReadFirstCell(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Array ||
            row.GetArrayLength() == 0 ||
            row[0].ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        return Normalize(row[0].GetString());
    }

    /// <summary>
    /// Reads a romanization row, which holds one cell per KRC syllable. Kugou already pads each cell
    /// with its own trailing space, so concatenating restores the spacing of the original line.
    /// </summary>
    private static string JoinSyllableCells(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var cell in row.EnumerateArray())
        {
            if (cell.ValueKind == JsonValueKind.String)
            {
                builder.Append(cell.GetString());
            }
        }

        return Normalize(builder.ToString());
    }

    private static string Normalize(string? text)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        return trimmed == EmptyTextMarker ? string.Empty : trimmed;
    }
}
