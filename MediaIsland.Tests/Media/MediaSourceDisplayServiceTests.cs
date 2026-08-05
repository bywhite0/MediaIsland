using MediaIsland.Services.Media.SourceDisplay;
using Xunit;

namespace MediaIsland.Tests.Media;

public class MediaSourceDisplayNameResolverTests
{
    private static MappedDisplayName Explicit(string value) => new(value, true);

    private static MappedDisplayName Fallback(string value) => new(value, false);

    [Fact]
    public void Resolve_CustomNameWinsOverEverything()
    {
        Assert.Equal(
            "工作音乐",
            MediaSourceDisplayNameResolver.Resolve(Explicit("Spotify"), "工作音乐", "Spotify for Windows"));
        Assert.Equal(
            "工作音乐",
            MediaSourceDisplayNameResolver.Resolve(Fallback("cloudmusic"), "工作音乐", "CloudMusic"));
    }

    [Fact]
    public void Resolve_ExplicitMappingWinsOverPlatformName()
    {
        // 网易云音乐进程描述名是 "CloudMusic"，内置映射应覆盖它。
        Assert.Equal(
            "网易云音乐",
            MediaSourceDisplayNameResolver.Resolve(Explicit("网易云音乐"), null, "CloudMusic"));
    }

    [Fact]
    public void Resolve_FallbackMapping_UsesPlatformNameWhenAvailable()
    {
        Assert.Equal(
            "Spotify for Windows",
            MediaSourceDisplayNameResolver.Resolve(Fallback("Spotify"), null, "Spotify for Windows"));
    }

    [Fact]
    public void Resolve_NoPlatformName_FallsBackToMapping()
    {
        Assert.Equal(
            "Spotify",
            MediaSourceDisplayNameResolver.Resolve(Fallback("Spotify"), " ", null));
        Assert.Equal(
            "Spotify",
            MediaSourceDisplayNameResolver.Resolve(Fallback("Spotify"), null, "   "));
    }
}
