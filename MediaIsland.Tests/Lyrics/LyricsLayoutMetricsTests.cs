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
    [InlineData(1, 10)]
    [InlineData(2, 10)]
    [InlineData(3, 8)]
    [InlineData(4, 8)]
    public void GetActiveLineFontSize_CompactsBackgroundLyricsWithTheFullLayout(
        int visibleLineCount,
        double expectedFontSize)
    {
        var fontSize = LyricsLayoutMetrics.GetActiveLineFontSize(
            defaultFontSize: 14,
            visibleLineCount,
            isBackground: true);

        Assert.Equal(expectedFontSize, fontSize);
    }
}
