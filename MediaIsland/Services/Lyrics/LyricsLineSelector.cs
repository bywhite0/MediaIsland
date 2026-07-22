using MediaIsland.Services.Lyrics.Models;

namespace MediaIsland.Services.Lyrics;

internal static class LyricsLineSelector
{
    public static IReadOnlyList<LyricsLineSelection> SelectActive(LyricsDocument lyrics, TimeSpan position)
    {
        var activeLines = lyrics.Lines
            .Select((line, index) => new IndexedLine(index, line))
            .Where(item => IsActive(item.Line, position))
            .ToArray();
        if (activeLines.Length == 0)
        {
            var fallback = FindFallbackLine(lyrics.Lines, position);
            return fallback == null
                ? []
                : [new LyricsLineSelection(fallback.Index, fallback.Line, fallback.Line.IsDuet)];
        }

        if (lyrics.Format != LyricsFormat.Ttml)
        {
            return activeLines
                .Select(item => new LyricsLineSelection(item.Index, item.Line, item.Line.IsDuet))
                .ToArray();
        }

        var foregroundLines = activeLines.Where(item => !item.Line.IsBackground).ToArray();
        if (foregroundLines.Length == 0)
        {
            return activeLines
                .Select(item => new LyricsLineSelection(item.Index, item.Line, item.Line.IsDuet))
                .ToArray();
        }

        // AMLL: a background line is shown when its previous non-background owner is active,
        // and each main line may have at most one background line below it.
        var activeBackgrounds = activeLines.Where(item => item.Line.IsBackground).ToArray();
        var usedBackgrounds = new HashSet<int>();
        var result = new List<LyricsLineSelection>(foregroundLines.Length * 2);
        foreach (var foreground in foregroundLines)
        {
            result.Add(new LyricsLineSelection(
                foreground.Index,
                foreground.Line,
                foreground.Line.IsDuet));

            var background = FindBackgroundForMain(lyrics.Lines, foreground, activeBackgrounds, usedBackgrounds);
            if (background == null)
            {
                continue;
            }

            usedBackgrounds.Add(background.Index);
            result.Add(new LyricsLineSelection(
                background.Index,
                background.Line,
                background.Line.IsDuet || foreground.Line.IsDuet));
        }

        return result;
    }

    private static IndexedLine? FindBackgroundForMain(
        IReadOnlyList<LyricsLine> lines,
        IndexedLine foreground,
        IReadOnlyList<IndexedLine> activeBackgrounds,
        HashSet<int> usedBackgrounds)
    {
        // Prefer the structural next line when it is a background vocal.
        var nextIndex = foreground.Index + 1;
        if (nextIndex < lines.Count && lines[nextIndex].IsBackground && !usedBackgrounds.Contains(nextIndex))
        {
            return new IndexedLine(nextIndex, lines[nextIndex]);
        }

        return activeBackgrounds
            .Where(item => !usedBackgrounds.Contains(item.Index) && Overlaps(foreground.Line, item.Line))
            .OrderByDescending(item => GetOverlapDuration(foreground.Line, item.Line))
            .ThenBy(item => Math.Abs((item.Line.StartTime - foreground.Line.StartTime).TotalMilliseconds))
            .FirstOrDefault();
    }

    private static IndexedLine? FindFallbackLine(IReadOnlyList<LyricsLine> lines, TimeSpan position)
    {
        IndexedLine? foreground = null;
        IndexedLine? fallback = null;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].StartTime > position)
            {
                break;
            }

            fallback = new IndexedLine(i, lines[i]);
            if (!lines[i].IsBackground)
            {
                foreground = fallback;
            }
        }

        return foreground ?? fallback;
    }

    private static bool IsActive(LyricsLine line, TimeSpan position) =>
        line.StartTime <= position &&
        line.EndTime > line.StartTime &&
        position < line.EndTime;

    private static bool Overlaps(LyricsLine first, LyricsLine second) =>
        first.EndTime > second.StartTime && second.EndTime > first.StartTime;

    private static double GetOverlapDuration(LyricsLine first, LyricsLine second) =>
        (Min(first.EndTime, second.EndTime) - Max(first.StartTime, second.StartTime)).TotalMilliseconds;

    private static TimeSpan Min(TimeSpan first, TimeSpan second) => first <= second ? first : second;

    private static TimeSpan Max(TimeSpan first, TimeSpan second) => first >= second ? first : second;

    private sealed record IndexedLine(int Index, LyricsLine Line);
}

internal sealed record LyricsLineSelection(
    int LineIndex,
    LyricsLine Line,
    bool IsDuetSide);
