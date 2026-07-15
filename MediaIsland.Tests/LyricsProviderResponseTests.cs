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

    [Fact]
    public void QqFallbackSearchResponse_ReadsSongIdAndMid()
    {
        const string body = """
            {
              "code": 0,
              "data": {
                "song": {
                  "list": [
                    {
                      "songid": 512505650,
                      "songmid": "003unZvq3vHMAr",
                      "songname": "眩耀夜行",
                      "albumname": "眩耀夜行",
                      "interval": 245,
                      "singer": [{ "name": "スリーズブーケ" }]
                    }
                  ]
                }
              }
            }
            """;

        var song = Assert.Single(QqMusicLyricsProvider.ParseFallbackSearchResponseBody(body));

        Assert.Equal("512505650", song.Id);
        Assert.Equal("003unZvq3vHMAr", song.Mid);
        Assert.Equal("眩耀夜行", song.Title);
        Assert.Equal("スリーズブーケ", song.Artist);
        Assert.Equal("眩耀夜行", song.Album);
        Assert.Equal(TimeSpan.FromSeconds(245), song.Duration);
    }

    [Fact]
    public void QqLyricContentExtraction_PreservesTimedLineBreaks()
    {
        const string decrypted = """
            <QrcInfos>
            <Lyric_1 LyricType="1" LyricContent="[0,1000]Hello(0,500) world(500,500)
            [1000,1000]Again(1000,1000)" />
            </QrcInfos>
            """;

        var content = QqMusicLyricsProvider.ExtractQrcLyricContent(decrypted);

        Assert.Equal(
            "[0,1000]Hello(0,500) world(500,500)\n[1000,1000]Again(1000,1000)",
            content);
    }

    [Fact]
    public void QqLyricContentExtraction_PreservesLiteralQuotesInMalformedAttribute()
    {
        const string decrypted = """
            <QrcInfos>
            <Lyric_1 LyricType="1" LyricContent="[990,4801]A(990,135)"(1126,135)B(1261,680)" />
            </QrcInfos>
            """;

        var content = QqMusicLyricsProvider.ExtractQrcLyricContent(decrypted);

        Assert.Equal("[990,4801]A(990,135)\"(1126,135)B(1261,680)", content);
    }
}
