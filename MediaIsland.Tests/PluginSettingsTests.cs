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
    public void WordLyricsEmphasisGlowEnabled_DefaultsToTrueAndPersists()
    {
        Assert.True(new PluginSettings().IsWordLyricsEmphasisGlowEnabled);

        var settings = new PluginSettings
        {
            IsWordLyricsEmphasisGlowEnabled = false
        };

        var json = JsonSerializer.Serialize(settings);
        var restored = JsonSerializer.Deserialize<PluginSettings>(json);

        Assert.Contains("\"IsWordLyricsEmphasisGlowEnabled\":false", json);
        Assert.NotNull(restored);
        Assert.False(restored!.IsWordLyricsEmphasisGlowEnabled);
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
    public void LyricsTransitionEnabled_DefaultsToTrueAndPersists()
    {
        Assert.True(new PluginSettings().IsLyricsTransitionEnabled);

        var settings = new PluginSettings
        {
            IsLyricsTransitionEnabled = false
        };

        var json = JsonSerializer.Serialize(settings);
        var restored = JsonSerializer.Deserialize<PluginSettings>(json);

        Assert.Contains("\"IsLyricsTransitionEnabled\":false", json);
        Assert.NotNull(restored);
        Assert.False(restored.IsLyricsTransitionEnabled);
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

    [Fact]
    public void SPlayerNextLyricsSearch_DefaultsToDisabledAndCanBeOverridden()
    {
        const string legacySettingsJson =
            "{\"MediaSourceList\":[{\"Source\":\"top.imsyy.splayer-next\"}]}";
        var settings = JsonSerializer.Deserialize<PluginSettings>(legacySettingsJson);
        var source = Assert.Single(settings!.MediaSourceList);

        Assert.False(source.IsLyricsSearchEnabled);

        source.IsLyricsSearchEnabled = true;
        var json = JsonSerializer.Serialize(settings);
        var restored = JsonSerializer.Deserialize<PluginSettings>(json);

        Assert.Contains("\"IsLyricsSearchEnabled\":true", json);
        Assert.True(Assert.Single(restored!.MediaSourceList).IsLyricsSearchEnabled);
    }

    [Fact]
    public void NotifyMediaSourceSettingsSaved_RaisesEvent()
    {
        var settings = new PluginSettings();
        var invocationCount = 0;
        settings.MediaSourceSettingsSaved += (_, _) => invocationCount++;

        settings.NotifyMediaSourceSettingsSaved();

        Assert.Equal(1, invocationCount);
    }
}
