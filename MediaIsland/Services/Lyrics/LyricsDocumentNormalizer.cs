using Lyricify.Lyrics.Models;
using MediaIsland.Services.Lyrics.Models;

namespace MediaIsland.Services.Lyrics;

public static class LyricsDocumentNormalizer
{
    public static LyricsDocument FromLyricify(
        LyricsData data,
        LyricsMetadata metadata,
        LyricsSourceId source,
        string providerItemId,
        LyricsFormat format,
        bool preferWordSync,
        IReadOnlyList<string>? translations = null,
        IReadOnlyList<string>? romanizations = null)
    {
        var lines = new List<LyricsLine>();
        var sourceLines = data.Lines ?? [];
        for (var i = 0; i < sourceLines.Count; i++)
        {
            var line = sourceLines[i];
            var start = Clamp(FromMilliseconds(line.StartTime));
            var end = line.EndTime.HasValue
                ? Clamp(FromMilliseconds(line.EndTime))
                : TimeSpan.Zero;
            if (end <= start)
            {
                end = i + 1 < sourceLines.Count && sourceLines[i + 1].StartTime.HasValue
                    ? Clamp(FromMilliseconds(sourceLines[i + 1].StartTime))
                    : (metadata.Duration ?? start);
                if (end < start)
                {
                    end = start;
                }
            }

            var words = new List<LyricsWord>();
            if (preferWordSync && line is SyllableLineInfo syllableLine && syllableLine.Syllables is { Count: > 0 })
            {
                for (var wordIndex = 0; wordIndex < syllableLine.Syllables.Count; wordIndex++)
                {
                    var syllable = syllableLine.Syllables[wordIndex];
                    var wordStart = Clamp(TimeSpan.FromMilliseconds(syllable.StartTime));
                    var wordEnd = Clamp(TimeSpan.FromMilliseconds(syllable.EndTime));
                    if (wordEnd < wordStart)
                    {
                        wordEnd = wordIndex + 1 < syllableLine.Syllables.Count
                            ? Clamp(TimeSpan.FromMilliseconds(syllableLine.Syllables[wordIndex + 1].StartTime))
                            : end;
                    }

                    if (wordEnd < wordStart)
                    {
                        wordEnd = wordStart;
                    }

                    words.Add(new LyricsWord(wordStart, wordEnd, syllable.Text ?? string.Empty));
                }
            }

            var text = line.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text) && words.Count > 0)
            {
                text = string.Concat(words.Select(word => word.Text));
            }

            string? translation = null;
            if (translations != null && i < translations.Count)
            {
                translation = translations[i];
            }
            else if (line.SubLine != null && !string.IsNullOrWhiteSpace(line.SubLine.Text))
            {
                translation = line.SubLine.Text;
            }

            string? romanization = null;
            if (romanizations != null && i < romanizations.Count)
            {
                romanization = romanizations[i];
            }

            lines.Add(new LyricsLine(
                start,
                end,
                text,
                words,
                translation,
                romanization));
        }

        lines = NormalizeLines(lines, metadata.Duration).ToList();
        var syncMode = preferWordSync && lines.Any(line => line.Words.Count > 0)
            ? LyricsSyncMode.Word
            : lines.Count > 0
                ? LyricsSyncMode.Line
                : LyricsSyncMode.Unsynced;

        if (!preferWordSync)
        {
            lines = lines.Select(line => line with { Words = Array.Empty<LyricsWord>() }).ToList();
            syncMode = lines.Count > 0 ? LyricsSyncMode.Line : LyricsSyncMode.Unsynced;
        }

        return new LyricsDocument(metadata, lines, syncMode, source, providerItemId, format);
    }

    public static LyricsDocument Create(
        IEnumerable<LyricsLine> lines,
        LyricsMetadata metadata,
        LyricsSourceId source,
        string providerItemId,
        LyricsFormat format,
        bool preferWordSync)
    {
        var normalized = NormalizeLines(lines, metadata.Duration).ToList();
        if (!preferWordSync)
        {
            normalized = normalized.Select(line => line with { Words = Array.Empty<LyricsWord>() }).ToList();
        }

        var syncMode = preferWordSync && normalized.Any(line => line.Words.Count > 0)
            ? LyricsSyncMode.Word
            : normalized.Count > 0
                ? LyricsSyncMode.Line
                : LyricsSyncMode.Unsynced;

        return new LyricsDocument(metadata, normalized, syncMode, source, providerItemId, format);
    }

    public static IReadOnlyList<LyricsLine> NormalizeLines(IEnumerable<LyricsLine> lines, TimeSpan? trackDuration)
    {
        var ordered = lines
            .Select(line => line with
            {
                StartTime = Clamp(line.StartTime),
                EndTime = Clamp(line.EndTime),
                Words = line.Words
                    .Select(word => new LyricsWord(Clamp(word.StartTime), Clamp(word.EndTime), word.Text ?? string.Empty))
                    .OrderBy(word => word.StartTime)
                    .ThenBy(word => word.EndTime)
                    .ToArray()
            })
            .OrderBy(line => line.StartTime)
            .ThenBy(line => line.EndTime)
            .ToList();

        for (var i = 0; i < ordered.Count; i++)
        {
            var line = ordered[i];
            var end = line.EndTime;
            if (end <= line.StartTime)
            {
                end = i + 1 < ordered.Count
                    ? ordered[i + 1].StartTime
                    : trackDuration ?? line.StartTime;
                if (end < line.StartTime)
                {
                    end = line.StartTime;
                }
            }

            var words = new List<LyricsWord>();
            for (var wordIndex = 0; wordIndex < line.Words.Count; wordIndex++)
            {
                var word = line.Words[wordIndex];
                var wordEnd = word.EndTime;
                if (wordEnd < word.StartTime)
                {
                    wordEnd = wordIndex + 1 < line.Words.Count
                        ? line.Words[wordIndex + 1].StartTime
                        : end;
                }

                if (wordEnd < word.StartTime)
                {
                    wordEnd = word.StartTime;
                }

                words.Add(word with { EndTime = wordEnd });
            }

            ordered[i] = line with { EndTime = end, Words = words };
        }

        // AMLL-style presentation tuning: pair main/BG windows, then advance starts for smoother switches.
        SyncMainAndBackgroundLines(ordered);
        TryAdvanceStartTime(ordered);
        return ordered;
    }

    /// <summary>
    /// Sync a main lyric line with the immediately following background vocal line by taking
    /// the earliest start and latest end so they appear and disappear together.
    /// </summary>
    private static void SyncMainAndBackgroundLines(IList<LyricsLine> lines)
    {
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (line.IsBackground)
            {
                continue;
            }

            if (i + 1 >= lines.Count || !lines[i + 1].IsBackground)
            {
                continue;
            }

            var background = lines[i + 1];
            var timedWords = line.Words
                .Concat(background.Words)
                .Where(word => !string.IsNullOrWhiteSpace(word.Text))
                .ToArray();
            if (timedWords.Length == 0)
            {
                continue;
            }

            var minStart = timedWords.Min(word => word.StartTime);
            var maxEnd = timedWords.Max(word => word.EndTime);
            var finalStart = Min(minStart, Min(line.StartTime, background.StartTime));
            var finalEnd = Max(maxEnd, Max(line.EndTime, background.EndTime));
            lines[i] = line with { StartTime = finalStart, EndTime = finalEnd };
            lines[i + 1] = background with { StartTime = finalStart, EndTime = finalEnd };
        }
    }

    /// <summary>
    /// Advance main-line start times by up to 600 ms for a more natural entrance.
    /// When the next line originally overlaps the previous one, fall back to 400 ms or
    /// 30% of the previous line duration, and keep a following background line aligned.
    /// </summary>
    private static void TryAdvanceStartTime(IList<LyricsLine> lines)
    {
        var defaultAdvance = TimeSpan.FromMilliseconds(600);
        var fallbackAdvance = TimeSpan.FromMilliseconds(400);
        const double fallbackAdvanceRatio = 0.3;

        var prevLineStartTime = TimeSpan.Zero;
        var prevLineEndTime = TimeSpan.Zero;
        var prevMainGroupStartTime = TimeSpan.Zero;
        var prevMainGroupEndTime = TimeSpan.Zero;
        var hasPrevLine = false;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.IsBackground)
            {
                continue;
            }

            var originalStartTime = line.StartTime;
            var originalEndTime = line.EndTime;

            TimeSpan targetAdvance;
            TimeSpan safeBoundary;
            if (hasPrevLine)
            {
                var originallyHadGap = originalStartTime >= prevLineEndTime;
                if (originallyHadGap)
                {
                    targetAdvance = defaultAdvance;
                    safeBoundary = prevMainGroupEndTime;
                }
                else
                {
                    targetAdvance = fallbackAdvance;
                    var prevDuration = prevLineEndTime - prevLineStartTime;
                    safeBoundary = prevLineStartTime +
                                   TimeSpan.FromMilliseconds(prevDuration.TotalMilliseconds * fallbackAdvanceRatio);
                }
            }
            else
            {
                targetAdvance = defaultAdvance;
                safeBoundary = TimeSpan.Zero;
            }

            var targetTime = line.StartTime - targetAdvance;
            var newStartTime = Max(safeBoundary, targetTime);
            if (newStartTime < TimeSpan.Zero)
            {
                newStartTime = TimeSpan.Zero;
            }

            if (newStartTime < line.StartTime)
            {
                line = line with { StartTime = newStartTime };
                lines[i] = line;
            }

            if (i + 1 < lines.Count && lines[i + 1].IsBackground)
            {
                lines[i + 1] = lines[i + 1] with { StartTime = line.StartTime };
            }

            if (hasPrevLine)
            {
                var overlapsPrevGroup =
                    originalStartTime < prevMainGroupEndTime &&
                    originalEndTime > prevMainGroupStartTime;
                if (overlapsPrevGroup)
                {
                    prevMainGroupStartTime = Min(prevMainGroupStartTime, originalStartTime);
                    prevMainGroupEndTime = Max(prevMainGroupEndTime, originalEndTime);
                }
                else
                {
                    prevMainGroupStartTime = originalStartTime;
                    prevMainGroupEndTime = originalEndTime;
                }
            }
            else
            {
                prevMainGroupStartTime = originalStartTime;
                prevMainGroupEndTime = originalEndTime;
            }

            prevLineStartTime = originalStartTime;
            prevLineEndTime = originalEndTime;
            hasPrevLine = true;
        }
    }

    private static TimeSpan FromMilliseconds(int? value) =>
        value.HasValue ? TimeSpan.FromMilliseconds(Math.Max(0, value.Value)) : TimeSpan.Zero;

    private static TimeSpan Clamp(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;

    private static TimeSpan Min(TimeSpan first, TimeSpan second) => first <= second ? first : second;

    private static TimeSpan Max(TimeSpan first, TimeSpan second) => first >= second ? first : second;
}
