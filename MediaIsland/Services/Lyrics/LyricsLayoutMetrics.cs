namespace MediaIsland.Services.Lyrics;

internal static class LyricsLayoutMetrics
{
    private const double BackgroundLyricsFontSize = 10;
    private const double CompactBackgroundLyricsFontSize = 8;
    private const double CompactMainLyricsFontSizeReduction = 4;
    private const double MinimumMainLyricsFontSize = 10;

    public static double GetActiveLineFontSize(
        double defaultFontSize,
        int visibleLineCount,
        bool isBackground)
    {
        if (isBackground)
        {
            return visibleLineCount > 2
                ? CompactBackgroundLyricsFontSize
                : BackgroundLyricsFontSize;
        }

        return visibleLineCount > 2
            ? Math.Max(MinimumMainLyricsFontSize, defaultFontSize - CompactMainLyricsFontSizeReduction)
            : defaultFontSize;
    }
}
