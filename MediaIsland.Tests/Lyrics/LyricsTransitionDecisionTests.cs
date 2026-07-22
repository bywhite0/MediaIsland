using MediaIsland.Services.Lyrics;
using Xunit;

namespace MediaIsland.Tests.Lyrics;

public class LyricsTransitionDecisionTests
{
    [Theory]
    [InlineData(new[] { 1 }, new[] { 1, 2 }, false)]
    [InlineData(new[] { 1, 2 }, new[] { 2 }, false)]
    [InlineData(new[] { 2 }, new[] { 2, 3 }, false)]
    [InlineData(new[] { 2, 3 }, new[] { 3 }, false)]
    [InlineData(new[] { 10, 11 }, new[] { 10, 11, 12 }, false)]
    [InlineData(new[] { 10, 11, 12 }, new[] { 12 }, false)]
    [InlineData(new[] { 1 }, new[] { 2 }, true)]
    [InlineData(new[] { 1, 2 }, new[] { 3, 4 }, true)]
    public void ShouldAnimateFullLineTransition_UsesSharedLineContinuity(
        int[] previous,
        int[] next,
        bool expected)
    {
        var actual = LyricsLayoutMetrics.ShouldAnimateFullLineTransition(
            isShowingActiveLines: true,
            previous,
            next);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ShouldAnimateFullLineTransition_ForcesWhenNotShowingActiveLines()
    {
        Assert.True(LyricsLayoutMetrics.ShouldAnimateFullLineTransition(
            isShowingActiveLines: false,
            previousIndices: [1],
            nextIndices: [1, 2]));
    }

    [Fact]
    public void ShouldAnimateFullLineTransition_ForcesWhenEitherSideEmpty()
    {
        Assert.True(LyricsLayoutMetrics.ShouldAnimateFullLineTransition(true, [], [1]));
        Assert.True(LyricsLayoutMetrics.ShouldAnimateFullLineTransition(true, [1], []));
    }
}
