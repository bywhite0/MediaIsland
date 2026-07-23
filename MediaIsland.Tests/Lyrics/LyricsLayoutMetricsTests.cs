using Avalonia.Media;
using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Lyrics.Models;
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
    [InlineData(1, 14, 10)]
    [InlineData(2, 14, 10)]
    [InlineData(1, 20, 14)]
    [InlineData(3, 14, 8.4)]
    [InlineData(4, 20, 12)]
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

    [Fact]
    public void ComputeMaxSongLineWidth_UsesLargestFontPerRole()
    {
        var measured = new List<(string Text, double FontSize)>();
        double Measure(string text, double fontSize)
        {
            measured.Add((text, fontSize));
            return text.Length * fontSize;
        }

        var width = LyricsLayoutMetrics.ComputeMaxSongLineWidth(
            [
                ("short", false),
                ("a much longer main line", false),
                ("background only", true)
            ],
            defaultFontSize: 14,
            Measure);

        Assert.Equal("a much longer main line".Length * 14, width);
        Assert.Contains(measured, item => item.Text == "background only" && item.FontSize == 10);
        Assert.Contains(measured, item => item.Text == "short" && item.FontSize == 14);
    }

    [Fact]
    public void ComputeMaxSongLineWidth_DoesNotCapMeasuredWidth()
    {
        var width = LyricsLayoutMetrics.ComputeMaxSongLineWidth(
            [("x", false)],
            defaultFontSize: 14,
            measureTextWidth: static (_, _) => 9999);

        Assert.Equal(9999, width);
    }

    [Fact]
    public void DocumentHasDuet_IsTrueWhenAnyLineIsDuet()
    {
        var lines = new[]
        {
            CreateLine(0, 2, "solo"),
            CreateLine(2, 4, "duet", isDuet: true)
        };

        Assert.True(LyricsLayoutMetrics.DocumentHasDuet(lines));
    }

    [Fact]
    public void DocumentHasDuet_IsFalseWithoutDuetLines()
    {
        var lines = new[]
        {
            CreateLine(0, 2, "main"),
            CreateLine(2, 4, "bg", isBackground: true)
        };

        Assert.False(LyricsLayoutMetrics.DocumentHasDuet(lines));
    }

    [Theory]
    [InlineData(false, false, TextAlignment.Center)]
    [InlineData(false, true, TextAlignment.Left)]
    [InlineData(true, false, TextAlignment.Right)]
    [InlineData(true, true, TextAlignment.Right)]
    public void ResolveLineTextAlignment_MatchesAmllDuetRules(
        bool isDuetSide,
        bool documentHasDuet,
        TextAlignment expected)
    {
        var alignment = LyricsLayoutMetrics.ResolveLineTextAlignment(isDuetSide, documentHasDuet);
        Assert.Equal(expected, alignment);
    }

    private static LyricsLine CreateLine(
        double startSeconds,
        double endSeconds,
        string text,
        bool isBackground = false,
        bool isDuet = false) =>
        new(
            TimeSpan.FromSeconds(startSeconds),
            TimeSpan.FromSeconds(endSeconds),
            text,
            [new LyricsWord(TimeSpan.FromSeconds(startSeconds), TimeSpan.FromSeconds(endSeconds), text)],
            IsBackground: isBackground,
            IsDuet: isDuet);
}
