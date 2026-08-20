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

    [Fact]
    public void PlaybackBufferMs_ClampsToNamedBounds()
    {
        // 与 AudioPlaybackSettingsTests 里那条 InlineData 版不重复：那条钉的是数值本身
        // （50 / 1000），这条钉的是「setter 用的是常量」。改常量时前者变红、后者保持绿，
        // 两条各自能被不同的变异杀掉。
        var settings = new PluginSettings();

        settings.MediaLinkPlaybackBufferMs = PluginSettings.MaxPlaybackBufferMs + 1;
        Assert.Equal(PluginSettings.MaxPlaybackBufferMs, settings.MediaLinkPlaybackBufferMs);

        settings.MediaLinkPlaybackBufferMs = PluginSettings.MinPlaybackBufferMs - 1;
        Assert.Equal(PluginSettings.MinPlaybackBufferMs, settings.MediaLinkPlaybackBufferMs);
    }

    [Fact]
    public void PlaybackBufferMs_DecimalMirrors_AgreeWithTheirSourceBounds()
    {
        // 设置页的 NumericUpDown 读的是镜像。镜像被改回字面量则真值源又成两份，
        // 而那份分裂在界面上没有任何提示。
        Assert.Equal((decimal)PluginSettings.MinPlaybackBufferMs, PluginSettings.PlaybackBufferMsMinimum);
        Assert.Equal((decimal)PluginSettings.MaxPlaybackBufferMs, PluginSettings.PlaybackBufferMsMaximum);
    }
}
