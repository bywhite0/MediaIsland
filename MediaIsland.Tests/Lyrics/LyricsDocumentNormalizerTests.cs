using Xunit;
using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Lyrics.Cleanup;
using MediaIsland.Services.Lyrics.Models;

namespace MediaIsland.Tests.Lyrics;

public class LyricsDocumentNormalizerTests
{
    [Fact]
    public void NormalizeLines_InfersMissingEnds_AndClampsNegative()
    {
        var lines = new[]
        {
            new LyricsLine(TimeSpan.FromMilliseconds(-10), TimeSpan.Zero, "a",
            [
                new LyricsWord(TimeSpan.FromMilliseconds(-5), TimeSpan.Zero, "a"),
                new LyricsWord(TimeSpan.FromMilliseconds(100), TimeSpan.Zero, "b")
            ]),
            new LyricsLine(TimeSpan.FromMilliseconds(500), TimeSpan.Zero, "c", Array.Empty<LyricsWord>())
        };

        var normalized = LyricsDocumentNormalizer.NormalizeLines(lines, TimeSpan.FromSeconds(3));
        Assert.Equal(2, normalized.Count);
        Assert.Equal(TimeSpan.Zero, normalized[0].StartTime);
        Assert.Equal(TimeSpan.FromMilliseconds(500), normalized[0].EndTime);
        Assert.Equal(TimeSpan.FromSeconds(3), normalized[1].EndTime);
        Assert.Equal(TimeSpan.Zero, normalized[0].Words[0].StartTime);
        Assert.True(normalized[0].Words[0].EndTime >= normalized[0].Words[0].StartTime);
        Assert.True(normalized[0].Words[1].EndTime >= normalized[0].Words[1].StartTime);
    }

    [Fact]
    public void Create_CanCollapseWordSync()
    {
        var lines = new[]
        {
            new LyricsLine(
                TimeSpan.FromMilliseconds(0),
                TimeSpan.FromMilliseconds(1000),
                "hello",
                [
                    new LyricsWord(TimeSpan.Zero, TimeSpan.FromMilliseconds(500), "hel"),
                    new LyricsWord(TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1000), "lo")
                ])
        };

        var document = LyricsDocumentNormalizer.Create(
            lines,
            new LyricsMetadata("t", "a", "b", TimeSpan.FromSeconds(1)),
            LyricsSourceId.QqMusic,
            "1",
            LyricsFormat.Qrc,
            preferWordSync: false);

        Assert.Equal(LyricsSyncMode.Line, document.SyncMode);
        Assert.Empty(document.Lines[0].Words);
    }
    [Fact]
    public void NormalizeLines_SyncsMainAndFollowingBackgroundWindows()
    {
        var lines = new[]
        {
            new LyricsLine(
                TimeSpan.FromMilliseconds(1000),
                TimeSpan.FromMilliseconds(2000),
                "main",
                [
                    new LyricsWord(TimeSpan.FromMilliseconds(1000), TimeSpan.FromMilliseconds(1500), "main")
                ]),
            new LyricsLine(
                TimeSpan.FromMilliseconds(1200),
                TimeSpan.FromMilliseconds(1800),
                "bg",
                [
                    new LyricsWord(TimeSpan.FromMilliseconds(1100), TimeSpan.FromMilliseconds(1900), "bg")
                ],
                IsBackground: true)
        };

        var normalized = LyricsDocumentNormalizer.NormalizeLines(lines, TimeSpan.FromSeconds(5));

        Assert.Equal(2, normalized.Count);
        Assert.Equal(normalized[0].StartTime, normalized[1].StartTime);
        Assert.Equal(normalized[0].EndTime, normalized[1].EndTime);
        // Shared end is the latest of words and both line ends (2000).
        Assert.Equal(TimeSpan.FromMilliseconds(2000), normalized[0].EndTime);
        // Start may be advanced up to 600ms for presentation; keep both aligned.
        Assert.True(normalized[0].StartTime <= TimeSpan.FromMilliseconds(1000));
    }

    [Fact]
    public void NormalizeLines_AdvancesMainLineStartWhenGapAllows()
    {
        var lines = new[]
        {
            new LyricsLine(
                TimeSpan.FromMilliseconds(0),
                TimeSpan.FromMilliseconds(1000),
                "first",
                [new LyricsWord(TimeSpan.Zero, TimeSpan.FromMilliseconds(1000), "first")]),
            new LyricsLine(
                TimeSpan.FromMilliseconds(3000),
                TimeSpan.FromMilliseconds(4000),
                "second",
                [
                    new LyricsWord(TimeSpan.FromMilliseconds(3000), TimeSpan.FromMilliseconds(4000), "second")
                ])
        };

        var normalized = LyricsDocumentNormalizer.NormalizeLines(lines, TimeSpan.FromSeconds(5));

        // First line can advance up to 600ms but is already at 0.
        Assert.Equal(TimeSpan.Zero, normalized[0].StartTime);
        // Second line has a gap, so it advances by 600ms from 3000 -> 2400.
        Assert.Equal(TimeSpan.FromMilliseconds(2400), normalized[1].StartTime);
        // Word timings stay original so word animation still starts on beat.
        Assert.Equal(TimeSpan.FromMilliseconds(3000), normalized[1].Words[0].StartTime);
    }

    [Fact]
    public void NormalizeLines_AdvancesBackgroundWithItsMainOwner()
    {
        var lines = new[]
        {
            new LyricsLine(
                TimeSpan.FromMilliseconds(0),
                TimeSpan.FromMilliseconds(1000),
                "first",
                [new LyricsWord(TimeSpan.Zero, TimeSpan.FromMilliseconds(1000), "first")]),
            new LyricsLine(
                TimeSpan.FromMilliseconds(3000),
                TimeSpan.FromMilliseconds(4000),
                "main",
                [new LyricsWord(TimeSpan.FromMilliseconds(3000), TimeSpan.FromMilliseconds(4000), "main")]),
            new LyricsLine(
                TimeSpan.FromMilliseconds(3100),
                TimeSpan.FromMilliseconds(3900),
                "bg",
                [new LyricsWord(TimeSpan.FromMilliseconds(3100), TimeSpan.FromMilliseconds(3900), "bg")],
                IsBackground: true)
        };

        var normalized = LyricsDocumentNormalizer.NormalizeLines(lines, TimeSpan.FromSeconds(5));

        Assert.Equal(normalized[1].StartTime, normalized[2].StartTime);
        Assert.True(normalized[1].StartTime < TimeSpan.FromMilliseconds(3000));
    }

    private static readonly LyricsCleanupOptions StripOnly = new() { StripCredits = true };

    private static LyricsLine TimedLine(int startMs, int endMs, string text) =>
        new(TimeSpan.FromMilliseconds(startMs), TimeSpan.FromMilliseconds(endMs), text, Array.Empty<LyricsWord>());

    [Fact]
    public void NormalizeLines_NullCleanup_MatchesLegacyOverload()
    {
        var lines = new[] { TimedLine(0, 5000, "作词：青石"), TimedLine(5200, 8000, "第一句") };

        var legacy = LyricsDocumentNormalizer.NormalizeLines(lines, TimeSpan.FromSeconds(10));
        var explicitNull = LyricsDocumentNormalizer.NormalizeLines(lines, TimeSpan.FromSeconds(10), cleanup: null);

        Assert.Equal(legacy.Select(l => (l.Text, l.StartTime, l.EndTime)), explicitNull.Select(l => (l.Text, l.StartTime, l.EndTime)));
        Assert.Equal(2, legacy.Count);
    }

    [Fact]
    public void NormalizeLines_RemovedCreditLine_DoesNotCapNextLineAdvance()
    {
        var lines = new[] { TimedLine(0, 5000, "作词：青石"), TimedLine(5200, 8000, "第一句") };

        var without = LyricsDocumentNormalizer.NormalizeLines(lines, TimeSpan.FromSeconds(10));
        var with = LyricsDocumentNormalizer.NormalizeLines(lines, TimeSpan.FromSeconds(10), StripOnly);

        // 不清理时提前量被前一组的结束时间（5000）截住；清理后按 600 ms 默认提前。
        Assert.Equal(TimeSpan.FromMilliseconds(5000), without[1].StartTime);
        Assert.Equal(["第一句"], with.Select(l => l.Text));
        Assert.Equal(TimeSpan.FromMilliseconds(4600), with[0].StartTime);
    }

    [Fact]
    public void NormalizeLines_InfersEndTimesBeforeStripping()
    {
        // 最后一句结束时间未知（0），其后是会被删掉的空行：结束时间仍应取空行的起点，而不是被拉到整首歌结束。
        var lines = new[] { TimedLine(1000, 0, "第一句"), TimedLine(3000, 0, "") };

        var normalized = LyricsDocumentNormalizer.NormalizeLines(lines, TimeSpan.FromSeconds(10), StripOnly);

        Assert.Equal(["第一句"], normalized.Select(l => l.Text));
        Assert.Equal(TimeSpan.FromMilliseconds(3000), normalized[0].EndTime);
    }

    [Fact]
    public void Create_FillsTitleAndArtistFromMetadata_ForTitleArtistLine()
    {
        var document = LyricsDocumentNormalizer.Create(
            [TimedLine(0, 1000, "春风十里 - 鹿先森乐队"), TimedLine(2000, 3000, "正文")],
            new LyricsMetadata("春风十里", "鹿先森乐队", null, TimeSpan.FromSeconds(10)),
            LyricsSourceId.Netease,
            "id",
            LyricsFormat.Lrc,
            preferWordSync: false,
            StripOnly);

        Assert.Equal(["正文"], document.Lines.Select(l => l.Text));
    }

    [Fact]
    public void Create_AllLinesRemoved_YieldsUnsyncedEmptyDocument()
    {
        var document = LyricsDocumentNormalizer.Create(
            [TimedLine(0, 1000, "作词：青石")],
            new LyricsMetadata("t", "a", null, null),
            LyricsSourceId.Netease,
            "id",
            LyricsFormat.Lrc,
            preferWordSync: false,
            StripOnly);

        Assert.Empty(document.Lines);
        Assert.Equal(LyricsSyncMode.Unsynced, document.SyncMode);
    }
}
