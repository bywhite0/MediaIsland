namespace MediaIsland.Services.Lyrics;

internal static class LyricsLayoutMetrics
{
    private const double BackgroundFontScale = 0.7;
    private const double CompactBackgroundFontScale = 0.6;
    private const double MinimumBackgroundFontSize = 10;
    private const double CompactMinimumBackgroundFontSize = 8;
    private const double CompactMainLyricsFontSizeReduction = 4;
    private const double MinimumMainLyricsFontSize = 10;

    public static double GetActiveLineFontSize(
        double defaultFontSize,
        int visibleLineCount,
        bool isBackground)
    {
        if (isBackground)
        {
            // AMLL: max(1em * 0.7, 10px); compact layouts shrink a bit further for the island host.
            if (visibleLineCount > 2)
            {
                return Math.Max(
                    CompactMinimumBackgroundFontSize,
                    defaultFontSize * CompactBackgroundFontScale);
            }

            return Math.Max(MinimumBackgroundFontSize, defaultFontSize * BackgroundFontScale);
        }

        return visibleLineCount > 2
            ? Math.Max(MinimumMainLyricsFontSize, defaultFontSize - CompactMainLyricsFontSizeReduction)
            : defaultFontSize;
    }

    /// <summary>
    /// Full-frame transition only when the active set has no continuity with the previous set.
    /// Partial handoffs (duet overlap, main+BG gaining/losing a second main) rebuild in place.
    /// </summary>
    public static bool ShouldAnimateFullLineTransition(
        bool isShowingActiveLines,
        IReadOnlyList<int> previousIndices,
        IReadOnlyList<int> nextIndices)
    {
        if (!isShowingActiveLines || previousIndices.Count == 0 || nextIndices.Count == 0)
        {
            return true;
        }

        return !previousIndices.Intersect(nextIndices).Any();
    }
}
