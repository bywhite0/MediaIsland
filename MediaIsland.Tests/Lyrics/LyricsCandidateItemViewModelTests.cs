using MediaIsland.Services.Lyrics.Models;
using MediaIsland.SettingsPages;
using Xunit;

namespace MediaIsland.Tests.Lyrics;

/// <summary>歌词候选表的「歌曲」列：主行为标题，副行为艺术家与专辑。</summary>
public class LyricsCandidateItemViewModelTests
{
    private static LyricsCandidateItemViewModel Create(string title, string artist, string album) =>
        new(new LyricsCandidate(
            LyricsSourceId.QqMusic,
            "qq-1",
            title,
            artist,
            album,
            TimeSpan.FromMinutes(3),
            90,
            SupportsWordSync: true));

    [Fact]
    public void Subtitle_WithAlbum_JoinsArtistAndAlbum()
    {
        var item = Create("ME!", "Taylor Swift", "Lover");

        Assert.Equal("Taylor Swift · Lover", item.Subtitle);
    }

    [Fact]
    public void Subtitle_WithoutAlbum_ShowsArtistOnly()
    {
        var item = Create("ME!", "Taylor Swift", "  ");

        Assert.Equal("Taylor Swift", item.Subtitle);
    }

    [Fact]
    public void Subtitle_AllBlank_FallsBackToPlaceholders()
    {
        var item = Create("", "", "");

        Assert.Equal("未知标题", item.Title);
        Assert.Equal("未知艺术家", item.Subtitle);
    }
}
