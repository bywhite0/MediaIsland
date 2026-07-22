using MediaIsland.Helpers;
using MediaIsland.Models;
using Xunit;

namespace MediaIsland.Tests.Media;

public class MediaSourceFilterTests
{
    [Fact]
    public void IsEnabled_ReturnsFalseForExplicitlyDisabledSource()
    {
        MediaSource[] sources =
        [
            new MediaSource { Source = "Spotify.exe", IsEnabled = false }
        ];

        Assert.False(MediaSourceFilter.IsEnabled("Spotify.exe", sources));
    }

    [Fact]
    public void IsEnabled_ReturnsTrueForUnlistedSource()
    {
        MediaSource[] sources =
        [
            new MediaSource { Source = "Spotify.exe", IsEnabled = false }
        ];

        Assert.True(MediaSourceFilter.IsEnabled("MSEdge", sources));
    }

    [Fact]
    public void IsLyricsSearchEnabled_DisablesSPlayerNextByDefault()
    {
        Assert.False(MediaSourceFilter.IsLyricsSearchEnabled("top.imsyy.splayer-next", []));
        Assert.True(MediaSourceFilter.IsLyricsSearchEnabled("Spotify.exe", []));
    }

    [Fact]
    public void IsLyricsSearchEnabled_UsesConfiguredOverride()
    {
        MediaSource[] sources =
        [
            new MediaSource
            {
                Source = "top.imsyy.splayer-next",
                IsLyricsSearchEnabled = true
            }
        ];

        Assert.True(MediaSourceFilter.IsLyricsSearchEnabled("top.imsyy.splayer-next", sources));
    }
}
