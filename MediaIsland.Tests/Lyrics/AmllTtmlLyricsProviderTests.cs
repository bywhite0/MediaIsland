using System.Text.Json;
using MediaIsland.Services.Lyrics.Providers;
using MediaIsland.Services.Media;
using Xunit;

namespace MediaIsland.Tests.Lyrics;

public class AmllTtmlLyricsProviderTests
{
    [Fact]
    public void TryGetItems_ReadsCurrentDataItemsEnvelope()
    {
        using var document = JsonDocument.Parse("""
            {
              "status": 200,
              "data": {
                "items": [
                  {
                    "filename": "test.ttml",
                    "musicNames": ["Song"]
                  }
                ]
              }
            }
            """);

        Assert.True(AmllTtmlLyricsProvider.TryGetItems(document.RootElement, out var items));
        Assert.Single(items.ToArray());
    }

    [Fact]
    public void ParseTtmlResponseBody_ReadsCurrentDataLyricsEnvelope()
    {
        const string ttml = "<tt xmlns=\"http://www.w3.org/ns/ttml\"></tt>";
        var body = JsonSerializer.Serialize(new
        {
            status = 200,
            data = new
            {
                filename = "test.ttml",
                lyrics = ttml,
                format = "ttml"
            }
        });

        Assert.Equal(ttml, AmllTtmlLyricsProvider.ParseTtmlResponseBody(body));
    }

    [Fact]
    public void BuildSearchQueries_ProgressivelyDropsAlbumAndCombinedArtistConstraints()
    {
        var media = new MediaInfo(
            "Spotify.exe",
            "Song (feat. Guest)",
            "Main Artist, Guest",
            "Spotify Release",
            TimeSpan.Zero,
            TimeSpan.FromMinutes(3),
            new MediaPlaybackInfo(MediaPlaybackState.Playing),
            null,
            null);

        var queries = AmllTtmlLyricsProvider.BuildSearchQueries(media);

        Assert.Contains(queries, query => query.Contains("albumName=Spotify%20Release"));
        Assert.Contains(queries, query =>
            query.Contains("artistName=Main%20Artist") && !query.Contains("albumName="));
        Assert.Contains(queries, query => query == "musicName=Song");
    }
}
