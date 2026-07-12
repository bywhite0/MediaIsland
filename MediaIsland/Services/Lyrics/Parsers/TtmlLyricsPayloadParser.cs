using System.Text.Json;
using MediaIsland.Services.Lyrics.Models;
using MediaIsland.Services.Lyrics.Native;

namespace MediaIsland.Services.Lyrics.Parsers;

public sealed class TtmlLyricsPayloadParser(TtmlNativeParser nativeParser) : ILyricsPayloadParser
{
    public bool CanParse(LyricsFormat format) => format == LyricsFormat.Ttml;

    public ValueTask<LyricsDocument> ParseAsync(LyricsPayload payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!nativeParser.IsAvailable)
        {
            throw new InvalidOperationException(nativeParser.FailureReason ?? "Native TTML parser unavailable.");
        }

        var json = nativeParser.ParseToAmllJson(payload.Content)
                   ?? throw new InvalidOperationException("Native TTML parser returned empty result.");

        cancellationToken.ThrowIfCancellationRequested();
        var document = AmllJsonConverter.ToDocument(
            json,
            payload.Metadata,
            payload.Source,
            payload.ProviderItemId,
            preferWordSync: true);
        return ValueTask.FromResult(document);
    }
}

internal static class AmllJsonConverter
{
    public static LyricsDocument ToDocument(
        string json,
        LyricsMetadata metadata,
        LyricsSourceId source,
        string providerItemId,
        bool preferWordSync)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var linesNode = FindLines(root);
        var lines = new List<LyricsLine>();

        if (linesNode.ValueKind == JsonValueKind.Array)
        {
            foreach (var lineNode in linesNode.EnumerateArray())
            {
                if (lineNode.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var start = ReadTime(lineNode, "startTime", "start_time", "start");
                var end = ReadTime(lineNode, "endTime", "end_time", "end");
                var text = ReadString(lineNode, "text", "words", "content") ?? string.Empty;
                var translation = ReadString(lineNode, "translatedLyric", "translation", "translated_lyric");
                var romanization = ReadString(lineNode, "romanLyric", "romanization", "roman_lyric");
                var isBackground = ReadBool(lineNode, "isBackground", "is_background", "isBG", "is_bg");
                var isDuet = ReadBool(lineNode, "isDuet", "is_duet");

                var words = new List<LyricsWord>();
                if (TryGetProperty(lineNode, out var wordsNode, "words", "syllables", "segments") &&
                    wordsNode.ValueKind == JsonValueKind.Array)
                {
                    foreach (var wordNode in wordsNode.EnumerateArray())
                    {
                        if (wordNode.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        var wordStart = ReadTime(wordNode, "startTime", "start_time", "start");
                        var wordEnd = ReadTime(wordNode, "endTime", "end_time", "end");
                        var wordText = ReadString(wordNode, "word", "text", "content") ?? string.Empty;
                        words.Add(new LyricsWord(wordStart, wordEnd, wordText));
                    }
                }

                if (string.IsNullOrWhiteSpace(text) && words.Count > 0)
                {
                    text = string.Concat(words.Select(word => word.Text));
                }

                lines.Add(new LyricsLine(
                    start,
                    end,
                    text,
                    words,
                    translation,
                    romanization,
                    isBackground,
                    isDuet));
            }
        }

        return LyricsDocumentNormalizer.Create(
            lines,
            metadata,
            source,
            providerItemId,
            LyricsFormat.Ttml,
            preferWordSync);
    }

    private static JsonElement FindLines(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root;
        }

        foreach (var key in new[] { "lyricLines", "lines", "lyric_lines", "data" })
        {
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty(key, out var node))
            {
                if (node.ValueKind == JsonValueKind.Array)
                {
                    return node;
                }

                if (node.ValueKind == JsonValueKind.Object &&
                    node.TryGetProperty("lines", out var nested) &&
                    nested.ValueKind == JsonValueKind.Array)
                {
                    return nested;
                }
            }
        }

        return default;
    }

    private static bool TryGetProperty(JsonElement element, out JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out value))
            {
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? ReadString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var node))
            {
                continue;
            }

            if (node.ValueKind == JsonValueKind.String)
            {
                return node.GetString();
            }

            if (node.ValueKind == JsonValueKind.Array)
            {
                // Some AMLL shapes store words as string arrays.
                var parts = node.EnumerateArray()
                    .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null)
                    .Where(item => !string.IsNullOrEmpty(item));
                var joined = string.Concat(parts);
                if (!string.IsNullOrEmpty(joined))
                {
                    return joined;
                }
            }
        }

        return null;
    }

    private static bool ReadBool(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var node) &&
                (node.ValueKind is JsonValueKind.True or JsonValueKind.False))
            {
                return node.GetBoolean();
            }
        }

        return false;
    }

    private static TimeSpan ReadTime(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var node))
            {
                continue;
            }

            if (node.ValueKind == JsonValueKind.Number)
            {
                if (node.TryGetInt64(out var msLong))
                {
                    return TimeSpan.FromMilliseconds(msLong);
                }

                if (node.TryGetDouble(out var msDouble))
                {
                    return TimeSpan.FromMilliseconds(msDouble);
                }
            }
            else if (node.ValueKind == JsonValueKind.String)
            {
                var text = node.GetString();
                if (double.TryParse(text, out var parsed))
                {
                    return TimeSpan.FromMilliseconds(parsed);
                }
            }
        }

        return TimeSpan.Zero;
    }
}
