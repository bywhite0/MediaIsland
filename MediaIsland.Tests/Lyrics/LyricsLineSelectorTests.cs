using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Lyrics.Models;
using Xunit;

namespace MediaIsland.Tests.Lyrics;

public class LyricsLineSelectorTests
{
    [Fact]
    public void SelectActive_GroupsWordSyncedBackgroundAfterItsForegroundLine()
    {
        var mainLine = CreateLine(10, 20, "main");
        var backgroundLine = CreateLine(12, 18, "background", isBackground: true);
        var document = CreateDocument(LyricsFormat.Ttml, mainLine, backgroundLine);

        var selection = LyricsLineSelector.SelectActive(document, TimeSpan.FromSeconds(14));

        Assert.Collection(
            selection,
            item => Assert.Same(mainLine, item.Line),
            item => Assert.Same(backgroundLine, item.Line));
        Assert.NotEmpty(selection[1].Line.Words);
    }

    [Fact]
    public void SelectActive_ReturnsAllSimultaneousForegroundLines()
    {
        var first = CreateLine(10, 20, "first");
        var second = CreateLine(12, 18, "second", isDuet: true);
        var document = CreateDocument(LyricsFormat.Ttml, first, second);

        var selection = LyricsLineSelector.SelectActive(document, TimeSpan.FromSeconds(14));

        Assert.Collection(
            selection,
            item =>
            {
                Assert.Same(first, item.Line);
                Assert.False(item.IsDuetSide);
            },
            item =>
            {
                Assert.Same(second, item.Line);
                Assert.True(item.IsDuetSide);
            });
    }

    [Fact]
    public void SelectActive_BackgroundFollowsItsDuetSide()
    {
        var duet = CreateLine(10, 20, "duet", isDuet: true);
        var background = CreateLine(12, 18, "background", isBackground: true);
        var document = CreateDocument(LyricsFormat.Ttml, duet, background);

        var selection = LyricsLineSelector.SelectActive(document, TimeSpan.FromSeconds(14));

        Assert.Equal(2, selection.Count);
        Assert.True(selection[0].IsDuetSide);
        Assert.True(selection[1].IsDuetSide);
    }

    [Fact]
    public void SelectActive_RemovesExpiredLines()
    {
        var mainLine = CreateLine(10, 20, "main");
        var backgroundLine = CreateLine(12, 18, "background", isBackground: true);
        var document = CreateDocument(LyricsFormat.Ttml, mainLine, backgroundLine);

        var selection = LyricsLineSelector.SelectActive(document, TimeSpan.FromSeconds(19));

        Assert.Single(selection);
        Assert.Same(mainLine, selection[0].Line);
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

    private static LyricsDocument CreateDocument(LyricsFormat format, params LyricsLine[] lines) =>
        new(
            new LyricsMetadata("title", "artist", "album", TimeSpan.FromMinutes(3)),
            lines,
            LyricsSyncMode.Word,
            LyricsSourceId.AmllTtml,
            "id",
            format);
}
