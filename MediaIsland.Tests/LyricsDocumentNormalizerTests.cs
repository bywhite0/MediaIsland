using Xunit;
using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Lyrics.Models;

namespace MediaIsland.Tests;

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
}
