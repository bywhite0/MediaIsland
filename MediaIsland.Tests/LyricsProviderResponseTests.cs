using MediaIsland.Services.Lyrics.Providers;
using Xunit;

namespace MediaIsland.Tests;

public class LyricsProviderResponseTests
{
    [Fact]
    public void NeteaseResponse_PreservesOriginalAndTranslationLyrics()
    {
        const string body = """
            {
              "lrc": { "lyric": "[00:01.00]original" },
              "tlyric": { "lyric": "[00:01.00]translation" }
            }
            """;

        var result = NeteaseLyricsProvider.ParseLyricResponseBody(body);

        Assert.NotNull(result);
        Assert.Equal("[00:01.00]original", result.Value.Lyric);
        Assert.Equal("[00:01.00]translation", result.Value.Translation);
    }

    [Fact]
    public void QqResponse_ReadsEncryptedOriginalAndTranslationFromCommentEnvelope()
    {
        const string body = """
            <!--
            <command>
              <miniversion="1" />
              <lyric>
                <content><![CDATA[ABC123]]></content>
                <contentts><![CDATA[DEF456]]></contentts>
              </lyric>
            </command>
            -->
            """;

        var result = QqMusicLyricsProvider.ParseQrcResponseBody(body);

        Assert.Equal("ABC123", result.Original);
        Assert.Equal("DEF456", result.Translation);
    }
}
