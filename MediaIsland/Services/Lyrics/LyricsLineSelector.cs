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

        var groupedBackgrounds = new Dictionary<int, List<IndexedLine>>();
        var ungroupedBackgrounds = new List<IndexedLine>();
        foreach (var background in activeLines.Where(item => item.Line.IsBackground))
        {
            var owner = FindBackgroundOwner(background.Line, foregroundLines);
            if (owner == null)
            {
                ungroupedBackgrounds.Add(background);
                continue;
            }

            if (!groupedBackgrounds.TryGetValue(owner.Index, out var group))
            {
                group = [];
                groupedBackgrounds[owner.Index] = group;
            }

            group.Add(background);
        }

        var result = new List<LyricsLineSelection>(activeLines.Length);
        foreach (var foreground in foregroundLines)
        {
            result.Add(new LyricsLineSelection(
                foreground.Index,
                foreground.Line,
                foreground.Line.IsDuet));
            if (!groupedBackgrounds.TryGetValue(foreground.Index, out var backgrounds))
            {
                continue;
            }

            result.AddRange(backgrounds.Select(background => new LyricsLineSelection(
                background.Index,
                background.Line,
                background.Line.IsDuet || foreground.Line.IsDuet)));
        }

        result.AddRange(ungroupedBackgrounds.Select(background => new LyricsLineSelection(
            background.Index,
            background.Line,
            background.Line.IsDuet)));
        return result;
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

    private static IndexedLine? FindBackgroundOwner(
        LyricsLine background,
        IReadOnlyList<IndexedLine> foregroundLines)
    {
        return foregroundLines
            .Where(item => Overlaps(item.Line, background))
            .OrderByDescending(item => GetOverlapDuration(item.Line, background))
            .ThenBy(item => Math.Abs((item.Line.StartTime - background.StartTime).TotalMilliseconds))
            .FirstOrDefault();
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
