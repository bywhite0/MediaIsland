using MediaIsland.Services.Media.SourceDisplay;
using Xunit;

namespace MediaIsland.Tests;

public class MediaSourceDisplayNameResolverTests
{
    [Fact]
    public void Resolve_PrefersCustomNameThenPlatformName()
    {
        Assert.Equal(
            "工作音乐",
            MediaSourceDisplayNameResolver.Resolve("Spotify", "工作音乐", "Spotify for Windows"));
        Assert.Equal(
            "Spotify for Windows",
            MediaSourceDisplayNameResolver.Resolve("Spotify", null, "Spotify for Windows"));
        Assert.Equal(
            "Spotify",
            MediaSourceDisplayNameResolver.Resolve("Spotify", " ", null));
    }
}
