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

    [Fact]
    public async Task ParseAsync_TranslationWithExtraLines_MatchesByTimestamp()
    {
        // QQ Music ships translation tracks that carry lines the lyric track does not have.
        // Index matching would shift every translation down by one.
        const string content = """
            [0,400]intro(0,400)
            [2000,1000]line-one(2000,1000)
            [7000,1000]line-two(7000,1000)
            """;
        const string translation = """
            [00:00.00]intro-translation
            [00:00.80]orphan line
            [00:02.00]译文一
            [00:07.00]译文二
            """;
        var payload = new LyricsPayload(
            LyricsFormat.Qrc,
            content,
            LyricsSourceId.QqMusic,
            "test",
            new LyricsMetadata("Song", "Artist", null, TimeSpan.FromSeconds(10)),
            translation);

        var document = await new ManagedLyricsPayloadParser().ParseAsync(payload, CancellationToken.None);

        Assert.Equal(3, document.Lines.Count);
        Assert.Equal("intro-translation", document.Lines[0].Translation);
        Assert.Equal("译文一", document.Lines[1].Translation);
        Assert.Equal("译文二", document.Lines[2].Translation);
    }

    [Fact]
    public async Task ParseAsync_TranslationMissingLine_LeavesGapWithoutShifting()
    {
        const string content = """
            [0,1000]line-one(0,1000)
            [2000,1000]line-two(2000,1000)
            [4000,1000]line-three(4000,1000)
            """;
        const string translation = """
            [00:00.00]译文一
            [00:04.00]译文三
            """;
        var payload = new LyricsPayload(
            LyricsFormat.Qrc,
            content,
            LyricsSourceId.QqMusic,
            "test",
            new LyricsMetadata("Song", "Artist", null, TimeSpan.FromSeconds(6)),
            translation);

        var document = await new ManagedLyricsPayloadParser().ParseAsync(payload, CancellationToken.None);

        Assert.Equal("译文一", document.Lines[0].Translation);
        Assert.Null(document.Lines[1].Translation);
        Assert.Equal("译文三", document.Lines[2].Translation);
    }

    [Fact]
    public async Task ParseAsync_TranslationTimestampDrift_StillMatchesWithinTolerance()
    {
        // QQ Music rounds translation timestamps to 10 ms while the QRC track keeps exact values.
        const string content = "[1547,1000]line-one(1547,1000)";
        const string translation = "[00:01.54]译文";
        var payload = new LyricsPayload(
            LyricsFormat.Qrc,
            content,
            LyricsSourceId.QqMusic,
            "test",
            new LyricsMetadata("Song", "Artist", null, TimeSpan.FromSeconds(4)),
            translation);

        var document = await new ManagedLyricsPayloadParser().ParseAsync(payload, CancellationToken.None);

        Assert.Equal("译文", Assert.Single(document.Lines).Translation);
    }

    [Fact]
    public async Task ParseAsync_TranslationFarFromLyricLine_IsNotAttached()
    {
        const string content = "[5000,1000]line-one(5000,1000)";
        const string translation = "[00:00.00]unrelated";
        var payload = new LyricsPayload(
            LyricsFormat.Qrc,
            content,
            LyricsSourceId.QqMusic,
            "test",
            new LyricsMetadata("Song", "Artist", null, TimeSpan.FromSeconds(8)),
            translation);

        var document = await new ManagedLyricsPayloadParser().ParseAsync(payload, CancellationToken.None);

        Assert.Null(Assert.Single(document.Lines).Translation);
    }

    [Fact]
    public async Task ParseAsync_EmptyTranslationMarker_LeavesLineUntranslated()
    {
        const string content = """
            [0,1000]line-one(0,1000)
            [2000,1000]line-two(2000,1000)
            """;
        const string translation = """
            [00:00.00]//
            [00:02.00]有翻译
            """;
        var payload = new LyricsPayload(
            LyricsFormat.Qrc,
            content,
            LyricsSourceId.QqMusic,
            "test",
            new LyricsMetadata("Song", "Artist", null, TimeSpan.FromSeconds(4)),
            translation);

        var document = await new ManagedLyricsPayloadParser().ParseAsync(payload, CancellationToken.None);

        Assert.Null(document.Lines[0].Translation);
        Assert.Equal("有翻译", document.Lines[1].Translation);
    }

    [Fact]
    public async Task ParseAsync_QrcRomanization_MatchesLyricLinesByTimestamp()
    {
        // QQ Music romanization is itself a word-synced QRC track sharing the lyric timeline.
        const string content = """
            [2000,1000]残酷(2000,500)な(2500,500)
            [7680,1000]少年(7680,1000)
            """;
        const string romanization = """
            [2000,1000]za n(2000,300) ko ku(2300,300) na(2600,400)
            [7680,1000]sho u ne n(7680,1000)
            """;
        var payload = new LyricsPayload(
            LyricsFormat.Qrc,
            content,
            LyricsSourceId.QqMusic,
            "test",
            new LyricsMetadata("Song", "Artist", null, TimeSpan.FromSeconds(10)),
            null,
            romanization);

        var document = await new ManagedLyricsPayloadParser().ParseAsync(payload, CancellationToken.None);

        Assert.Equal("za n ko ku na", document.Lines[0].Romanization);
        Assert.Equal("sho u ne n", document.Lines[1].Romanization);
    }
}
