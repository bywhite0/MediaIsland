using Avalonia;
using MediaIsland.Components;
using MediaIsland.Services.Lyrics.Models;
using Xunit;

namespace MediaIsland.Tests;

public class WordLyricsPresenterTests
{
    [Fact]
    public void LineSpringResponse_UsesTheWholeLineDuration()
    {
        var start = TimeSpan.FromSeconds(10);
        var end = TimeSpan.FromSeconds(14);

        Assert.Equal(0, WordLyricsPresenter.GetLineSpringResponse(start, start, end), 6);
        Assert.Equal(
            WordLyricsPresenter.GetSpringResponse(0.5),
            WordLyricsPresenter.GetLineSpringResponse(TimeSpan.FromSeconds(12), start, end),
            6);
        Assert.Equal(1, WordLyricsPresenter.GetLineSpringResponse(end, start, end), 6);
    }

    [Fact]
    public void SpringResponse_RisesPastTheTargetBeforeSettling()
    {
        var peak = Enumerable.Range(1, 98)
            .Select(index => WordLyricsPresenter.GetSpringResponse(index / 100.0))
            .Max();

        Assert.True(peak > 1);
        Assert.Equal(1, WordLyricsPresenter.GetSpringResponse(1), 6);
    }

    [Fact]
    public void EndingEmphasis_RequiresMoreThanOneSecond()
    {
        var oneSecondWord = new LyricsWord(
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(3),
            "hold");
        var longWord = oneSecondWord with { EndTime = TimeSpan.FromSeconds(4) };

        Assert.Equal(
            1,
            WordLyricsPresenter.GetEndingEmphasisScale(TimeSpan.FromSeconds(2.5), oneSecondWord),
            6);
        Assert.True(
            WordLyricsPresenter.GetEndingEmphasisScale(TimeSpan.FromSeconds(3), longWord) > 1);
        Assert.Equal(
            1,
            WordLyricsPresenter.GetEndingEmphasisScale(longWord.EndTime, longWord),
            6);
    }

    [Fact]
    public void EndingEmphasisScale_KeepsTheWordCenterFixed()
    {
        var center = new Point(20, 10);
        var matrix = WordLyricsPresenter.CreateScaleAround(1.05, center);

        Assert.Equal(center, matrix.Transform(center));
        Assert.Equal(new Point(30.5, 10), matrix.Transform(new Point(30, 10)));
    }
}
