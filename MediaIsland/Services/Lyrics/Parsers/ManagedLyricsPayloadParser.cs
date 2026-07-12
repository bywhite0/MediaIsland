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
        LyricsData data = payload.Format switch
        {
            LyricsFormat.Lrc => LrcParser.Parse(payload.Content.AsSpan()),
            LyricsFormat.Qrc => QrcParser.Parse(payload.Content),
            LyricsFormat.Krc => KrcParser.Parse(NormalizeKrcContent(payload.Content)),
            _ => throw new NotSupportedException($"Unsupported managed lyrics format: {payload.Format}")
        };

        IReadOnlyList<string>? translations = null;
        if (!string.IsNullOrWhiteSpace(payload.TranslationContent))
        {
            if (payload.Format == LyricsFormat.Qrc)
            {
                try
                {
                    var translated = QrcParser.Parse(payload.TranslationContent);
                    translations = translated.Lines?.Select(line => line.Text ?? string.Empty).ToArray();
                }
                catch
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
            }
            else
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
