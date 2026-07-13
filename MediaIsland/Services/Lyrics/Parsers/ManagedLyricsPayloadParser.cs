using Lyricify.Lyrics.Decrypter.Krc;
using Lyricify.Lyrics.Decrypter.Qrc;
using KrcDecrypter = Lyricify.Lyrics.Decrypter.Krc.Decrypter;
using QrcDecrypter = Lyricify.Lyrics.Decrypter.Qrc.Decrypter;
using Lyricify.Lyrics.Models;
using Lyricify.Lyrics.Parsers;
using MediaIsland.Services.Lyrics.Models;
using System.Text.RegularExpressions;

namespace MediaIsland.Services.Lyrics.Parsers;

public sealed class ManagedLyricsPayloadParser : ILyricsPayloadParser
{
    private static readonly Regex QrcLineRegex = new(
        @"^\s*\[(?<start>\d+)\s*,\s*(?<duration>\d+)\](?<content>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex QrcWordRegex = new(
        @"(?<text>.*?)(?:\((?<start>\d+)\s*,\s*(?<duration>\d+)\))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public bool CanParse(LyricsFormat format) =>
        format is LyricsFormat.Lrc or LyricsFormat.Qrc or LyricsFormat.Krc;

    public ValueTask<LyricsDocument> ParseAsync(LyricsPayload payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (payload.Format == LyricsFormat.Qrc)
        {
            return ValueTask.FromResult(ParseQrc(payload));
        }

        LyricsData data = payload.Format switch
        {
            LyricsFormat.Lrc => LrcParser.Parse(payload.Content.AsSpan()),
            LyricsFormat.Krc => KrcParser.Parse(NormalizeKrcContent(payload.Content)),
            _ => throw new NotSupportedException($"Unsupported managed lyrics format: {payload.Format}")
        };

        IReadOnlyList<string>? translations = null;
        if (!string.IsNullOrWhiteSpace(payload.TranslationContent))
        {
            try
            {
                var translated = LrcParser.Parse(payload.TranslationContent.AsSpan());
                translations = translated.Lines?.Select(line => line.Text ?? string.Empty).ToArray();
            }
            catch
            {
                translations = null;
            }
        }

        var preferWordSync = payload.Format is LyricsFormat.Qrc or LyricsFormat.Krc;
        var document = LyricsDocumentNormalizer.FromLyricify(
            data,
            payload.Metadata,
            payload.Source,
            payload.ProviderItemId,
            payload.Format,
            preferWordSync,
            translations);
        return ValueTask.FromResult(document);
    }

    private static LyricsDocument ParseQrc(LyricsPayload payload)
    {
        var lines = ParseQrcLines(payload.Content).ToArray();
        var translations = ParseQrcTranslationLines(payload.TranslationContent);
        if (translations != null)
        {
            lines = lines.Select((line, index) => index < translations.Count
                ? line with { Translation = translations[index] }
                : line).ToArray();
        }

        return LyricsDocumentNormalizer.Create(
            lines,
            payload.Metadata,
            payload.Source,
            payload.ProviderItemId,
            payload.Format,
            preferWordSync: true);
    }

    private static IReadOnlyList<LyricsLine> ParseQrcLines(string content)
    {
        var lines = new List<LyricsLine>();
        foreach (var rawLine in NormalizeQrcContent(content).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var lineMatch = QrcLineRegex.Match(rawLine);
            if (!lineMatch.Success ||
                !int.TryParse(lineMatch.Groups["start"].Value, out var startMilliseconds) ||
                !int.TryParse(lineMatch.Groups["duration"].Value, out var durationMilliseconds))
            {
                continue;
            }

            var contentText = lineMatch.Groups["content"].Value;
            var words = new List<LyricsWord>();
            foreach (Match wordMatch in QrcWordRegex.Matches(contentText))
            {
                if (!int.TryParse(wordMatch.Groups["start"].Value, out var wordStartMilliseconds) ||
                    !int.TryParse(wordMatch.Groups["duration"].Value, out var wordDurationMilliseconds))
                {
                    continue;
                }

                var wordStart = TimeSpan.FromMilliseconds(Math.Max(0, wordStartMilliseconds));
                words.Add(new LyricsWord(
                    wordStart,
                    wordStart.Add(TimeSpan.FromMilliseconds(Math.Max(0, wordDurationMilliseconds))),
                    wordMatch.Groups["text"].Value));
            }

            var lineStart = TimeSpan.FromMilliseconds(Math.Max(0, startMilliseconds));
            var text = words.Count > 0
                ? string.Concat(words.Select(word => word.Text))
                : contentText.Trim();
            lines.Add(new LyricsLine(
                lineStart,
                lineStart.Add(TimeSpan.FromMilliseconds(Math.Max(0, durationMilliseconds))),
                text,
                words));
        }

        return lines;
    }

    private static IReadOnlyList<string>? ParseQrcTranslationLines(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var qrcLines = ParseQrcLines(content);
        if (qrcLines.Count > 0)
        {
            return qrcLines.Select(line => line.Text).ToArray();
        }

        try
        {
            var translated = LrcParser.Parse(content.AsSpan());
            return translated.Lines?.Select(line => line.Text ?? string.Empty).ToArray();
        }
        catch
        {
            return null;
        }
    }

    public static string? DecryptQrc(string encrypted)
    {
        if (string.IsNullOrWhiteSpace(encrypted))
        {
            return null;
        }

        try
        {
            return QrcDecrypter.DecryptLyrics(encrypted);
        }
        catch
        {
            return null;
        }
    }

    public static string? DecryptKrc(string encrypted)
    {
        if (string.IsNullOrWhiteSpace(encrypted))
        {
            return null;
        }

        try
        {
            return KrcDecrypter.DecryptLyrics(encrypted);
        }
        catch
        {
            return null;
        }
    }

    internal static string NormalizeKrcContent(string content)
    {
        var timedLines = content
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(IsTimedKrcLine);
        return string.Join('\n', timedLines);
    }

    internal static string NormalizeQrcContent(string content)
    {
        return content
            .Replace("\\r\\n", "\n", StringComparison.Ordinal)
            .Replace("\\n", "\n", StringComparison.Ordinal)
            .Replace("\\r", "\n", StringComparison.Ordinal)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
    }

    private static bool IsTimedKrcLine(string line)
    {
        if (line.Length < 5 || line[0] != '[')
        {
            return false;
        }

        var comma = line.IndexOf(',', 1);
        var closeBracket = line.IndexOf(']', comma + 1);
        return comma > 1 &&
               closeBracket > comma + 1 &&
               int.TryParse(line.AsSpan(1, comma - 1), out _) &&
               int.TryParse(line.AsSpan(comma + 1, closeBracket - comma - 1), out _);
    }
}
