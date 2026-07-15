using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Lyrics.Models;
using Xunit;

namespace MediaIsland.Tests;

public class LyricsInterludeDetectorTests
{
    [Fact]
    public void ComputeCurrentInterlude_UsesPreviousEndAndNextStartMinusLeadTime()
    {
        var lines = new[]
        {
            CreateLine(0, 1, "first"),
            CreateLine(6, 7, "next", isDuet: true)
        };

        var interlude = LyricsInterludeDetector.ComputeCurrentInterlude(
            lines,
            TimeSpan.FromSeconds(3),
            0);

        Assert.NotNull(interlude);
        Assert.Equal(TimeSpan.FromSeconds(1), interlude.StartTime);
        Assert.Equal(TimeSpan.FromMilliseconds(5750), interlude.EndTime);
        Assert.Equal(0, interlude.AnchorLineIndex);
        Assert.True(interlude.IsNextDuet);
    }

    [Fact]
    public void ComputeCurrentInterlude_RequiresAtLeastFourSecondsAfterTheLeadTime()
    {
        var lines = new[]
        {
            CreateLine(0, 1, "first"),
            CreateLine(4.9, 5.5, "next")
        };

        var interlude = LyricsInterludeDetector.ComputeCurrentInterlude(
            lines,
            TimeSpan.FromSeconds(3),
            0);

        Assert.Null(interlude);
    }

    [Fact]
    public void ComputeCurrentInterlude_ChecksTheNeighbouringAnchorWhenTheActiveIndexMoves()
    {
        var lines = new[]
        {
            CreateLine(0, 1, "first"),
            CreateLine(6, 7, "next"),
            CreateLine(8, 9, "later")
        };

        var interlude = LyricsInterludeDetector.ComputeCurrentInterlude(
            lines,
            TimeSpan.FromSeconds(3),
            1);

        Assert.NotNull(interlude);
        Assert.Equal(0, interlude.AnchorLineIndex);
    }

    [Fact]
    public void ComputeCurrentInterlude_StopsBeforeTheNextLineLeadTime()
    {
        var lines = new[]
        {
            CreateLine(0, 1, "first"),
            CreateLine(6, 7, "next")
        };

        var interlude = LyricsInterludeDetector.ComputeCurrentInterlude(
            lines,
            TimeSpan.FromMilliseconds(5900),
            0);

        Assert.Null(interlude);
    }

    [Fact]
    public void ComputeNextLinePreview_ReplacesTheInterludeAtTheLeadTime()
    {
        var lines = new[]
        {
            CreateLine(0, 1, "first"),
            CreateLine(6, 7, "next", isDuet: true)
        };

        var preview = LyricsInterludeDetector.ComputeNextLinePreview(
            lines,
            TimeSpan.FromMilliseconds(5750),
            0);

        Assert.NotNull(preview);
        Assert.Equal(1, preview.LineIndex);
        Assert.Equal("next", preview.Line.Text);
        Assert.True(preview.IsDuetSide);
    }

    [Fact]
    public void ComputeNextLinePreview_DoesNotAppearBeforeTheInterludeEnds()
    {
        var lines = new[]
        {
            CreateLine(0, 1, "first"),
            CreateLine(6, 7, "next")
        };

        var preview = LyricsInterludeDetector.ComputeNextLinePreview(
            lines,
            TimeSpan.FromMilliseconds(5749),
            0);

        Assert.Null(preview);
    }

    private static LyricsLine CreateLine(
        double startSeconds,
        double endSeconds,
        string text,
        bool isDuet = false) =>
        new(
            TimeSpan.FromSeconds(startSeconds),
            TimeSpan.FromSeconds(endSeconds),
            text,
            [],
            IsDuet: isDuet);
}
