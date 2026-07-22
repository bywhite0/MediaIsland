using MediaIsland.Services.Lyrics;
using Xunit;

namespace MediaIsland.Tests.Lyrics;

public class LyricsLayoutMetricsTests
{
    [Theory]
    [InlineData(1, 14)]
    [InlineData(2, 14)]
    [InlineData(3, 10)]
    [InlineData(4, 10)]
    public void GetActiveLineFontSize_CompactsMainLyricsAfterTwoVisibleLines(
        int visibleLineCount,
        double expectedFontSize)
    {
        var fontSize = LyricsLayoutMetrics.GetActiveLineFontSize(
            defaultFontSize: 14,
            visibleLineCount,
            isBackground: false);

        Assert.Equal(expectedFontSize, fontSize);
    }

    [Theory]
    [InlineData(1, 14, 10)]   // max(14 * 0.7, 10) = 10
    [InlineData(2, 14, 10)]
    [InlineData(1, 20, 14)]   // 20 * 0.7 = 14
    [InlineData(3, 14, 8.4)]  // compact: max(8, 14 * 0.6) = 8.4
    [InlineData(4, 20, 12)]   // compact: 20 * 0.6 = 12
    public void GetActiveLineFontSize_ScalesBackgroundLyricsLikeAmll(
        int visibleLineCount,
        double defaultFontSize,
        double expectedFontSize)
    {
        var fontSize = LyricsLayoutMetrics.GetActiveLineFontSize(
            defaultFontSize,
            visibleLineCount,
            isBackground: true);

        Assert.Equal(expectedFontSize, fontSize);
    }
}
