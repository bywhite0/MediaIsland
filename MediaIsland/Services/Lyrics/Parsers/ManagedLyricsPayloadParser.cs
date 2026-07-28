using MediaIsland.Services.Lyrics.Models;

namespace MediaIsland.Services.Lyrics.Parsers;

/// <summary>
/// Turns LRC, QRC and KRC payloads into normalized <see cref="LyricsDocument"/> values.
/// </summary>
public sealed class ManagedLyricsPayloadParser : ILyricsPayloadParser
{
    /// <summary>
    /// Providers ship translation/romanization tracks whose line count can differ from the lyric
    /// track, so secondary lines are matched by timestamp instead of by index. Observed drift
    /// between corresponding lines is under 10 ms.
    /// </summary>
    private static readonly TimeSpan SecondaryLineTolerance = TimeSpan.FromMilliseconds(200);

    /// <summary>QQ Music and Kugou both mark a line that has no translation as "//".</summary>
    private const string EmptyTextMarker = "//";

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

    /// <summary>
    /// Attaches a translation or romanization track by matching timestamps. Index matching would
    /// shift the whole track whenever the secondary line count differs from the lyric line count.
    /// </summary>
    private static IReadOnlyList<LyricsLine> AttachSecondaryLines(
        IReadOnlyList<LyricsLine> lines,
        string? content,
        Func<LyricsLine, string, LyricsLine> attach)
    {
        var secondary = ParseSecondaryLines(content);
        if (lines.Count == 0 || secondary.Count == 0)
        {
            return lines;
        }

        var ordered = secondary.OrderBy(line => line.StartTime).ToArray();
        var result = lines.ToArray();
        var next = 0;

        for (var i = 0; i < result.Length && next < ordered.Length; i++)
        {
            var start = result[i].StartTime;

            // Drop secondary lines that sit before this lyric line and have no counterpart.
            while (next < ordered.Length && ordered[next].StartTime < start - SecondaryLineTolerance)
            {
                next++;
            }

            if (next >= ordered.Length || ordered[next].StartTime > start + SecondaryLineTolerance)
            {
                continue;
            }

            var text = ordered[next].Text?.Trim() ?? string.Empty;
            next++;
            if (text.Length > 0 && text != EmptyTextMarker)
            {
                result[i] = attach(result[i], text);
            }
        }

        return result;
    }

    private static IReadOnlyList<LyricsLine> ParseSecondaryLines(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        // QQ Music may return encrypted QRC, plaintext QRC, or plain LRC for translation/roma tracks.
        var qrcLines = QrcLyricsParser.Parse(content);
        return qrcLines.Count > 0 ? qrcLines : LrcLyricsParser.Parse(content);
    }
}
