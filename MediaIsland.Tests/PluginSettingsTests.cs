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
}
