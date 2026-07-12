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
}
