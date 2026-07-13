using MediaIsland.Services.Lyrics.Models;
using MediaIsland.Services.Lyrics.Parsers;
using Xunit;

namespace MediaIsland.Tests;

public class ManagedLyricsPayloadParserTests
{
    [Fact]
    public async Task ParseAsync_Krc_IgnoresMetadataLines()
    {
        const string content = """
            id:$00000000]
            [ar:Artist]
            [ti:Song]
            [offset:0]
            [1000,1000]<0,500,0>Hello<500,500,0> world
            """;
        var payload = new LyricsPayload(
            LyricsFormat.Krc,
            content,
            LyricsSourceId.Kugou,
            "test",
            new LyricsMetadata("Song", "Artist", null, TimeSpan.FromSeconds(3)));

        var document = await new ManagedLyricsPayloadParser().ParseAsync(payload, CancellationToken.None);

        Assert.Equal(LyricsSyncMode.Word, document.SyncMode);
        Assert.Single(document.Lines);
        Assert.Equal(2, document.Lines[0].Words.Count);
    }

    [Fact]
    public async Task ParseAsync_Qrc_ProducesWordSyncedLines()
    {
        const string content = "[0,1000]Hello(0,500) world(500,500)";
        var payload = new LyricsPayload(
            LyricsFormat.Qrc,
            content,
            LyricsSourceId.QqMusic,
            "test",
            new LyricsMetadata("Song", "Artist", null, TimeSpan.FromSeconds(3)));

        var document = await new ManagedLyricsPayloadParser().ParseAsync(payload, CancellationToken.None);

        Assert.Equal(LyricsSyncMode.Word, document.SyncMode);
        Assert.Single(document.Lines);
        Assert.Equal("Hello world", document.Lines[0].Text);
        Assert.Equal(2, document.Lines[0].Words.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(500), document.Lines[0].Words[0].EndTime);
    }

    [Fact]
    public async Task ParseAsync_Qrc_SeparatesEscapedLineBreaksBeforeParsingWords()
    {
        const string content = "[0,1000]Hello(0,500) world(500,500)\\n[1000,1000]Again(1000,1000)";
        var payload = new LyricsPayload(
            LyricsFormat.Qrc,
            content,
            LyricsSourceId.QqMusic,
            "test",
            new LyricsMetadata("Song", "Artist", null, TimeSpan.FromSeconds(3)));

        var document = await new ManagedLyricsPayloadParser().ParseAsync(payload, CancellationToken.None);

        Assert.Equal(LyricsSyncMode.Word, document.SyncMode);
        Assert.Equal(2, document.Lines.Count);
        Assert.Equal("Hello world", document.Lines[0].Text);
        Assert.Equal("Again", document.Lines[1].Text);
        Assert.DoesNotContain("[1000,1000]", document.Lines[0].Text);
    }

    [Fact]
    public async Task ParseAsync_Qrc_ParsesTimingAndWordsWithoutKeepingQrcTags()
    {
        const string content = """
            [ti:Song]
            [0,1000]Hello(0,500) world(500,500)
            [1000,1000]Again(1000,500) now(1500,500)
            """;
        var payload = new LyricsPayload(
            LyricsFormat.Qrc,
            content,
            LyricsSourceId.QqMusic,
            "test",
            new LyricsMetadata("Song", "Artist", null, TimeSpan.FromSeconds(3)));

        var document = await new ManagedLyricsPayloadParser().ParseAsync(payload, CancellationToken.None);

        Assert.Equal(LyricsSyncMode.Word, document.SyncMode);
        Assert.Equal(2, document.Lines.Count);
        Assert.Equal("Hello world", document.Lines[0].Text);
        Assert.Equal("Again now", document.Lines[1].Text);
        Assert.Equal(TimeSpan.FromSeconds(1), document.Lines[1].StartTime);
        Assert.DoesNotContain("[", document.Lines[0].Text);
    }
}
