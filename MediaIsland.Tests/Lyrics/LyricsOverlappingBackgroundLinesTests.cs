using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Lyrics.Models;
using Xunit;

namespace MediaIsland.Tests.Lyrics;

/// <summary>
/// 回归：抱きしめる花びら（AMLL TTML）在 01:18.139-01:19.533 与 02:43.007-02:44.698
/// 两处出现两行主歌词各带一行背景人声，共 4 行同时活跃。
/// 时间轴取自真实歌词文件，用于锁定「第二个背景行不再溢出」的行为。
/// </summary>
public class LyricsOverlappingBackgroundLinesTests
{
    private const double IslandFontSize = 16;

    private static LyricsLine Line(string begin, string end, string text, bool isBackground) =>
        new(TimeSpan.Parse(begin), TimeSpan.Parse(end), text, [], IsBackground: isBackground);

    private static LyricsDocument CreateDocument() =>
        new(
            new LyricsMetadata("抱きしめる花びら", "蓮ノ空女学院スクールアイドルクラブ", null, null),
            [
                Line("00:01:15.012", "00:01:19.533", "約束は", false),
                Line("00:01:15.012", "00:01:19.533", "約束は", true),
                Line("00:01:18.139", "00:01:21.550", "これからも", false),
                Line("00:01:18.139", "00:01:21.550", "ずっと", true)
            ],
            LyricsSyncMode.Word,
            LyricsSourceId.AmllTtml,
            "regression",
            LyricsFormat.Ttml);

    [Fact]
    public void SelectActive_YieldsTwoMainLinesEachWithItsBackgroundLine()
    {
        var active = LyricsLineSelector.SelectActive(CreateDocument(), TimeSpan.Parse("00:01:18.500"));

        Assert.Equal(4, active.Count);
        Assert.Equal([0, 1, 2, 3], active.Select(item => item.LineIndex));
        Assert.Equal([false, true, false, true], active.Select(item => item.Line.IsBackground));
    }

    [Fact]
    public void ResolveFitPlan_KeepsAllFourLinesAtIslandBodyFontSize()
    {
        var active = LyricsLineSelector.SelectActive(CreateDocument(), TimeSpan.Parse("00:01:18.500"));
        var isBackground = active.Select(item => item.Line.IsBackground).ToArray();

        var plan = LyricsLayoutMetrics.ResolveFitPlan(isBackground, IslandFontSize, lineSpacing: 0);

        Assert.All(plan.KeepLine, Assert.True);
        Assert.All(plan.FontSizes, size => Assert.True(size > 0));

        var extent = plan.FontSizes.Sum() * LyricsLayoutMetrics.LineHeightFactor;
        Assert.True(
            extent <= LyricsLayoutMetrics.GetVerticalBudget(IslandFontSize) + 0.01,
            $"extent {extent} exceeds budget {LyricsLayoutMetrics.GetVerticalBudget(IslandFontSize)}");
    }

    [Fact]
    public void ResolveFitPlan_FallsBackToThreeLinesOnSmallerIslandFont()
    {
        var active = LyricsLineSelector.SelectActive(CreateDocument(), TimeSpan.Parse("00:01:18.500"));
        var isBackground = active.Select(item => item.Line.IsBackground).ToArray();

        var plan = LyricsLayoutMetrics.ResolveFitPlan(isBackground, defaultFontSize: 14, lineSpacing: 0);

        Assert.Equal([true, true, true, false], plan.KeepLine);
    }
}
