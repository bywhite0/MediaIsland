using MediaIsland.Services.Lyrics;
using Xunit;

namespace MediaIsland.Tests.Lyrics;

/// <summary>
/// 活跃歌词行的纵向适配：先等比缩小，缩到可读性下限仍装不下时丢弃末尾背景人声行。
/// </summary>
public class LyricsFitPlanTests
{
    private const double IslandFontSize = 16;

    [Fact]
    public void ResolveFitPlan_ReturnsEmptyPlanForNoLines()
    {
        var plan = LyricsLayoutMetrics.ResolveFitPlan([], IslandFontSize, lineSpacing: 0);

        Assert.Empty(plan.KeepLine);
        Assert.Empty(plan.FontSizes);
        Assert.Equal(1, plan.Scale);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResolveFitPlan_KeepsSingleLineUnscaled(bool isBackground)
    {
        var plan = LyricsLayoutMetrics.ResolveFitPlan([isBackground], IslandFontSize, lineSpacing: 0);

        Assert.True(plan.KeepLine[0]);
        Assert.Equal(1, plan.Scale);
        Assert.Equal(
            LyricsLayoutMetrics.GetActiveLineFontSize(IslandFontSize, 1, isBackground),
            plan.FontSizes[0]);
    }

    [Fact]
    public void ResolveFitPlan_KeepsMainAndBackgroundPairUnscaled()
    {
        var plan = LyricsLayoutMetrics.ResolveFitPlan([false, true], IslandFontSize, lineSpacing: 0);

        Assert.All(plan.KeepLine, Assert.True);
        Assert.Equal(1, plan.Scale);
    }

    [Fact]
    public void ResolveFitPlan_KeepsThreeCompactLinesUnscaled()
    {
        var plan = LyricsLayoutMetrics.ResolveFitPlan([false, true, false], IslandFontSize, lineSpacing: 0);

        Assert.All(plan.KeepLine, Assert.True);
        Assert.Equal(1, plan.Scale);
    }

    /// <summary>
    /// 回归：两行主歌词各带一行背景人声时，第二个背景行不再被裁掉。
    /// </summary>
    [Fact]
    public void ResolveFitPlan_KeepsSecondBackgroundLineByScalingDown()
    {
        var isBackground = new[] { false, true, false, true };

        var plan = LyricsLayoutMetrics.ResolveFitPlan(isBackground, IslandFontSize, lineSpacing: 0);

        Assert.All(plan.KeepLine, Assert.True);
        Assert.True(plan.Scale < 1);
        Assert.True(GetExtent(plan, isBackground, lineSpacing: 0)
                    <= LyricsLayoutMetrics.GetVerticalBudget(IslandFontSize) + 0.01);
    }

    [Fact]
    public void ResolveFitPlan_KeepsScaledFontSizesAboveReadabilityFloor()
    {
        var isBackground = new[] { false, true, false, true };

        var plan = LyricsLayoutMetrics.ResolveFitPlan(isBackground, IslandFontSize, lineSpacing: 0);

        for (var i = 0; i < isBackground.Length; i++)
        {
            var floor = isBackground[i]
                ? LyricsLayoutMetrics.MinimumScaledBackgroundFontSize
                : LyricsLayoutMetrics.MinimumScaledMainFontSize;
            Assert.True(plan.FontSizes[i] >= floor, $"line {i}: {plan.FontSizes[i]} < {floor}");
        }
    }

    /// <summary>
    /// 岛屿字号偏小时缩放会击穿可读性下限，此时降级为三行。
    /// </summary>
    [Fact]
    public void ResolveFitPlan_DropsTrailingBackgroundLineWhenScalingWouldBeUnreadable()
    {
        var plan = LyricsLayoutMetrics.ResolveFitPlan(
            [false, true, false, true],
            defaultFontSize: 14,
            lineSpacing: 0);

        Assert.Equal([true, true, true, false], plan.KeepLine);
        Assert.Equal(0, plan.FontSizes[3]);
    }

    [Fact]
    public void ResolveFitPlan_DropsTrailingBackgroundLineNotTheFirstOne()
    {
        var plan = LyricsLayoutMetrics.ResolveFitPlan(
            [false, true, false, true],
            defaultFontSize: 14,
            lineSpacing: 0);

        Assert.True(plan.KeepLine[1]);
        Assert.False(plan.KeepLine[3]);
    }

    [Fact]
    public void ResolveFitPlan_NegativeLineSpacingLeavesRoomForLargerFonts()
    {
        var isBackground = new[] { false, true, false, true };

        var tight = LyricsLayoutMetrics.ResolveFitPlan(isBackground, IslandFontSize, lineSpacing: -2);
        var neutral = LyricsLayoutMetrics.ResolveFitPlan(isBackground, IslandFontSize, lineSpacing: 0);

        Assert.All(tight.KeepLine, Assert.True);
        Assert.True(tight.Scale > neutral.Scale);
    }

    [Fact]
    public void ResolveFitPlan_PositiveLineSpacingForcesDegradation()
    {
        var plan = LyricsLayoutMetrics.ResolveFitPlan(
            [false, true, false, true],
            IslandFontSize,
            lineSpacing: 3);

        Assert.False(plan.KeepLine[3]);
    }

    [Fact]
    public void ResolveFitPlan_NeverDropsEveryLine()
    {
        var isBackground = Enumerable.Repeat(true, 12).ToArray();

        var plan = LyricsLayoutMetrics.ResolveFitPlan(isBackground, defaultFontSize: 10, lineSpacing: 8);

        Assert.Contains(plan.KeepLine, keep => keep);
        Assert.All(plan.FontSizes.Where((_, i) => plan.KeepLine[i]), size => Assert.True(size > 0));
    }

    [Fact]
    public void ResolveFitPlan_ZeroesFontSizeForDroppedLines()
    {
        var plan = LyricsLayoutMetrics.ResolveFitPlan(
            [false, true, false, true],
            defaultFontSize: 14,
            lineSpacing: 0);

        for (var i = 0; i < plan.KeepLine.Count; i++)
        {
            if (!plan.KeepLine[i])
            {
                Assert.Equal(0, plan.FontSizes[i]);
            }
        }
    }

    private static double GetExtent(LyricsFitPlan plan, IReadOnlyList<bool> isBackground, double lineSpacing)
    {
        var keptCount = plan.KeepLine.Count(keep => keep);
        var extent = 0d;
        for (var i = 0; i < isBackground.Count; i++)
        {
            if (plan.KeepLine[i])
            {
                extent += plan.FontSizes[i] * LyricsLayoutMetrics.LineHeightFactor;
            }
        }

        return extent + (lineSpacing * Math.Max(0, keptCount - 1));
    }
}
