using MediaIsland.Services.Lyrics.Models;

namespace MediaIsland.Services.Lyrics;

/// <summary>
/// Finds long pauses between adjacent lyric lines that should render as an interlude.
/// </summary>
internal static class LyricsInterludeDetector
{
    private static readonly TimeSpan MinimumInterludeDuration = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan InterludeEndLeadTime = TimeSpan.FromMilliseconds(250);

    public static LyricsInterlude? ComputeCurrentInterlude(
        IReadOnlyList<LyricsLine> lines,
        TimeSpan position,
        int currentIndex)
    {
        if (lines.Count == 0)
        {
            return null;
        }

        // The active-line selection can lag one line while the component switches visual trees.
        // Inspect the neighbouring gaps as AMLL does so the interlude remains stable at that boundary.
        var anchorIndex = Math.Clamp(currentIndex, 0, lines.Count - 1);
        for (var candidateIndex = anchorIndex - 1; candidateIndex <= anchorIndex + 1; candidateIndex++)
        {
            var interlude = TryGetInterlude(lines, candidateIndex);
            if (interlude != null &&
                position > interlude.StartTime &&
                position < interlude.EndTime)
            {
                return interlude;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the next lyric line during the lead time after an interlude animation ends.
    /// This prevents the fallback selector from briefly restoring the previous line.
    /// </summary>
    public static LyricsLineSelection? ComputeNextLinePreview(
        IReadOnlyList<LyricsLine> lines,
        TimeSpan position,
        int currentIndex)
    {
        if (lines.Count == 0)
        {
            return null;
        }

        var anchorIndex = Math.Clamp(currentIndex, 0, lines.Count - 1);
        for (var candidateIndex = anchorIndex - 1; candidateIndex <= anchorIndex + 1; candidateIndex++)
        {
            var interlude = TryGetInterlude(lines, candidateIndex);
            if (interlude == null)
            {
                continue;
            }

            var nextLineIndex = interlude.AnchorLineIndex + 1;
            var nextLine = lines[nextLineIndex];
            if (position >= interlude.EndTime && position < nextLine.StartTime)
            {
                return new LyricsLineSelection(nextLineIndex, nextLine, nextLine.IsDuet);
            }
        }

        return null;
    }

    private static LyricsInterlude? TryGetInterlude(
        IReadOnlyList<LyricsLine> lines,
        int anchorIndex)
    {
        if (anchorIndex < -1 || anchorIndex >= lines.Count - 1)
        {
            return null;
        }

        var previousLine = anchorIndex == -1 ? null : lines[anchorIndex];
        var nextLine = lines[anchorIndex + 1];
        var gapStart = previousLine?.EndTime ?? TimeSpan.Zero;
        var gapEnd = Max(gapStart, nextLine.StartTime - InterludeEndLeadTime);
        if (gapEnd - gapStart < MinimumInterludeDuration)
        {
            return null;
        }

        return new LyricsInterlude(gapStart, gapEnd, anchorIndex, nextLine.IsDuet);
    }

    private static TimeSpan Max(TimeSpan first, TimeSpan second) => first >= second ? first : second;
}

internal sealed record LyricsInterlude(
    TimeSpan StartTime,
    TimeSpan EndTime,
    int AnchorLineIndex,
    bool IsNextDuet);
