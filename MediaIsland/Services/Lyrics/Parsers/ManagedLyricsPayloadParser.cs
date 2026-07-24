using Lyricify.Lyrics.Decrypter.Krc;
using Lyricify.Lyrics.Decrypter.Qrc;
using KrcDecrypter = Lyricify.Lyrics.Decrypter.Krc.Decrypter;
using QrcDecrypter = Lyricify.Lyrics.Decrypter.Qrc.Decrypter;
using Lyricify.Lyrics.Models;
using Lyricify.Lyrics.Parsers;
using MediaIsland.Services.Lyrics.Models;

namespace MediaIsland.Services.Lyrics.Parsers;

public sealed class ManagedLyricsPayloadParser : ILyricsPayloadParser
{
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

        var translations = ParseSecondaryLines(payload.TranslationContent);
        var romanizations = ParseSecondaryLines(payload.RomanizationContent);

        var preferWordSync = payload.Format is LyricsFormat.Qrc or LyricsFormat.Krc;
        var document = LyricsDocumentNormalizer.FromLyricify(
            data,
            payload.Metadata,
            payload.Source,
            payload.ProviderItemId,
            payload.Format,
            preferWordSync,
            translations,
            romanizations);
        return ValueTask.FromResult(document);
    }

    private static LyricsDocument ParseQrc(LyricsPayload payload)
    {
        var lines = ParseQrcLines(payload.Content).ToArray();
        lines = AttachSecondaryLines(lines, payload.TranslationContent, static (line, text) => line with { Translation = text });
        lines = AttachSecondaryLines(lines, payload.RomanizationContent, static (line, text) => line with { Romanization = text });

        return LyricsDocumentNormalizer.Create(
            lines,
            payload.Metadata,
            payload.Source,
            payload.ProviderItemId,
            payload.Format,
            preferWordSync: true);
    }

    private static LyricsLine[] AttachSecondaryLines(
        LyricsLine[] lines,
        string? content,
        Func<LyricsLine, string, LyricsLine> attach)
    {
        var secondary = ParseSecondaryLines(content);
        if (secondary == null)
        {
            return lines;
        }

        return lines.Select((line, index) => index < secondary.Count
            ? attach(line, secondary[index])
            : line).ToArray();
    }

    private static IReadOnlyList<string>? ParseSecondaryLines(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        // QQ Music may return encrypted QRC, plaintext QRC, or plain LRC for translation/roma tracks.
        var qrcLines = ParseQrcLines(content);
        if (qrcLines.Count > 0)
        {
            return qrcLines.Select(line => line.Text).ToArray();
        }

        try
        {
            var parsed = LrcParser.Parse(content.AsSpan());
            var lines = parsed.Lines?.Select(line => line.Text ?? string.Empty).ToArray();
            return lines is { Length: > 0 } ? lines : null;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<LyricsLine> ParseQrcLines(string content)
    {
        var lines = new List<LyricsLine>();
        foreach (var rawLine in content.Split(
                     ["\r\n", "\n", "\r", "\\r\\n", "\\n", "\\r"],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryParseQrcLine(rawLine, out var startMilliseconds, out var durationMilliseconds, out var contentText))
            {
                continue;
            }

            var words = ParseQrcWords(contentText);

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

        return lines;
    }

    private static bool TryParseQrcLine(
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

    private static IReadOnlyList<LyricsWord> ParseQrcWords(string content)
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
        int.TryParse(value, out milliseconds) && milliseconds >= 0;


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
