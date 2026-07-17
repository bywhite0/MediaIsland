using System.Text.Json;
using MediaIsland.Models;
using Xunit;

namespace MediaIsland.Tests;

public class PluginSettingsTests
{
    [Fact]
    public void WordLyricsEnabled_DefaultsToTrueAndPersists()
    {
        Assert.True(new PluginSettings().IsWordLyricsEnabled);

        var settings = new PluginSettings
        {
            IsWordLyricsEnabled = false
        };

        var json = JsonSerializer.Serialize(settings);
        var restored = JsonSerializer.Deserialize<PluginSettings>(json);

        Assert.Contains("\"IsWordLyricsEnabled\":false", json);
        Assert.NotNull(restored);
        Assert.False(restored.IsWordLyricsEnabled);
    }

    [Fact]
    public void LyricsInterludeAnimationEnabled_DefaultsToTrueAndPersists()
    {
        Assert.True(new PluginSettings().IsLyricsInterludeAnimationEnabled);

        var settings = new PluginSettings
        {
            IsLyricsInterludeAnimationEnabled = false
        };

        var json = JsonSerializer.Serialize(settings);
        var restored = JsonSerializer.Deserialize<PluginSettings>(json);

        Assert.Contains("\"IsLyricsInterludeAnimationEnabled\":false", json);
        Assert.NotNull(restored);
        Assert.False(restored.IsLyricsInterludeAnimationEnabled);
    }

    [Fact]
    public void MediaSourceCustomDisplayName_PersistsAndOverridesResolvedName()
    {
        var settings = new PluginSettings
        {
            MediaSourceList =
            [
                new MediaSource
                {
                    Source = "Spotify.exe",
                    CustomDisplayName = "  工作音乐  "
                }
            ]
        };

        var json = JsonSerializer.Serialize(settings);
        var restored = JsonSerializer.Deserialize<PluginSettings>(json);

        Assert.Contains("\"CustomDisplayName\"", json);
        var source = Assert.Single(restored!.MediaSourceList);
        Assert.Equal("工作音乐", source.CustomDisplayName);
        Assert.Equal("工作音乐", source.DisplayName);

        source.CustomDisplayName = " ";
        Assert.Null(source.CustomDisplayName);
        Assert.Equal("Spotify.exe", source.DisplayName);
    }
}
