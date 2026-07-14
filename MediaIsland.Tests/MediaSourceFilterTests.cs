using MediaIsland.Helpers;
using MediaIsland.Models;
using Xunit;

namespace MediaIsland.Tests;

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
}
