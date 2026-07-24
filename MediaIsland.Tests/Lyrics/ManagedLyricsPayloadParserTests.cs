using MediaIsland.Services.Lyrics.Models;
using MediaIsland.Services.Lyrics.Parsers;
using Xunit;

namespace MediaIsland.Tests.Lyrics;

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

    [Fact]
    public async Task ParseAsync_Qrc_AttachesPlainLrcTranslationAndQrcRomanization()
    {
        const string content = """
            [0,1000]line-one(0,1000)
            [1000,1000]line-two(1000,1000)
            """;
        const string translation = """
            [00:00.00]translation one
            [00:01.00]translation two
            """;
        const string romanization = """
            [0,1000]roma one(0,1000)
            [1000,1000]roma two(1000,1000)
            """;
        var payload = new LyricsPayload(
            LyricsFormat.Qrc,
            content,
            LyricsSourceId.QqMusic,
            "test",
            new LyricsMetadata("Song", "Artist", null, TimeSpan.FromSeconds(3)),
            translation,
            romanization);

        var document = await new ManagedLyricsPayloadParser().ParseAsync(payload, CancellationToken.None);

        Assert.Equal(2, document.Lines.Count);
        Assert.Equal("line-one", document.Lines[0].Text);
        Assert.Equal("translation one", document.Lines[0].Translation);
        Assert.Equal("roma one", document.Lines[0].Romanization);
        Assert.Equal("line-two", document.Lines[1].Text);
        Assert.Equal("translation two", document.Lines[1].Translation);
        Assert.Equal("roma two", document.Lines[1].Romanization);
    }

    [Fact]
    public async Task ParseAsync_Qrc_PreservesLiteralQuotesAsWords()
    {
        const string content = "[0,1000]A(0,300)\"(300,200)B(500,500)";
        var payload = new LyricsPayload(
            LyricsFormat.Qrc,
            content,
            LyricsSourceId.QqMusic,
            "test",
            new LyricsMetadata("Song", "Artist", null, TimeSpan.FromSeconds(3)));

        var document = await new ManagedLyricsPayloadParser().ParseAsync(payload, CancellationToken.None);

        var line = Assert.Single(document.Lines);
        Assert.Equal("A\"B", line.Text);
        Assert.Collection(
            line.Words,
            word => Assert.Equal("A", word.Text),
            word => Assert.Equal("\"", word.Text),
            word => Assert.Equal("B", word.Text));
    }
}
