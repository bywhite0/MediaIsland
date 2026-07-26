using MediaIsland.Services.Lyrics.Models;

namespace MediaIsland.Services.Lyrics.Parsers;

/// <summary>
/// Turns LRC, QRC and KRC payloads into normalized <see cref="LyricsDocument"/> values.
/// </summary>
public sealed class ManagedLyricsPayloadParser : ILyricsPayloadParser
{
    public bool CanParse(LyricsFormat format) =>
        format is LyricsFormat.Lrc or LyricsFormat.Qrc or LyricsFormat.Krc;

    public ValueTask<LyricsDocument> ParseAsync(LyricsPayload payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var lines = payload.Format switch
        {
            LyricsFormat.Lrc => LrcLyricsParser.Parse(payload.Content),
            LyricsFormat.Qrc => QrcLyricsParser.Parse(payload.Content),
            LyricsFormat.Krc => KrcLyricsParser.Parse(payload.Content),
            _ => throw new NotSupportedException($"Unsupported managed lyrics format: {payload.Format}")
        };

        lines = AttachSecondaryLines(lines, payload.TranslationContent, static (line, text) => line with { Translation = text });
        lines = AttachSecondaryLines(lines, payload.RomanizationContent, static (line, text) => line with { Romanization = text });

        var document = LyricsDocumentNormalizer.Create(
            lines,
            payload.Metadata,
            payload.Source,
            payload.ProviderItemId,
            payload.Format,
            preferWordSync: payload.Format is LyricsFormat.Qrc or LyricsFormat.Krc);
        return ValueTask.FromResult(document);
    }

    private static IReadOnlyList<LyricsLine> AttachSecondaryLines(
        IReadOnlyList<LyricsLine> lines,
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
        var qrcLines = QrcLyricsParser.Parse(content);
        if (qrcLines.Count > 0)
        {
            return qrcLines.Select(line => line.Text).ToArray();
        }

        var lrcLines = LrcLyricsParser.Parse(content);
        return lrcLines.Count > 0 ? lrcLines.Select(line => line.Text).ToArray() : null;
    }
}
