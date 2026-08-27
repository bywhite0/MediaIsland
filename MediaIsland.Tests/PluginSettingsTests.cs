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

    [Fact]
    public void ManualOffsetInterval_IsPlusMinus500Ms_WithAgreeingMirrors()
    {
        // ±500 是需求给定的数：盖住蓝牙的 100 到 200 毫秒并留余量。字面量断言，
        // 常量被改动时这里必红——夹紧那条用的也是同一对常量，两条一起红才说明
        // 「区间只有一个真值源」还成立。
        Assert.Equal(-500, PluginSettings.MinManualOffsetMs);
        Assert.Equal(500, PluginSettings.MaxManualOffsetMs);
        Assert.Equal((decimal)PluginSettings.MinManualOffsetMs, PluginSettings.ManualOffsetMsMinimum);
        Assert.Equal((decimal)PluginSettings.MaxManualOffsetMs, PluginSettings.ManualOffsetMsMaximum);
    }

    [Fact]
    public void ManualOffset_IsClampedIntoTheInterval_OnBothWriteAndRead()
    {
        // 期望值全用字面量而不引用常量：这条要能被「改常量」的变异杀掉。
        // 断言若与 setter 同引用一个常量，常量被改时两边同步，这条恒绿——
        // 恒绿恰好说明它证不出「夹紧引用的就是那对常量」。
        var settings = new PluginSettings();

        // 写侧：越界夹紧而不拒绝，与播放缓冲的既有约定一致。
        settings.SetManualOffsetMs("dev-a", 9_999);
        Assert.Equal(500, settings.GetManualOffsetMs("dev-a"));
        settings.SetManualOffsetMs("dev-a", -9_999);
        Assert.Equal(-500, settings.GetManualOffsetMs("dev-a"));

        // 读侧：配置文件是整个字典一次性进来的，不走逐项 setter，出界只有读侧能拦。
        settings.MediaLinkManualOffsetsMs = new Dictionary<string, int>
        {
            ["dev-b"] = 40_000,
            ["dev-c"] = -40_000
        };
        Assert.Equal(500, settings.GetManualOffsetMs("dev-b"));
        Assert.Equal(-500, settings.GetManualOffsetMs("dev-c"));
    }

    [Fact]
    public void ManualOffsets_AreIsolatedPerDevice()
    {
        // 蓝牙耳机调好的值对音箱就是错的。两个设备各写各的，互不影响。
        var settings = new PluginSettings();

        settings.SetManualOffsetMs("dev-a", 120);
        settings.SetManualOffsetMs("dev-b", -80);

        Assert.Equal(120, settings.GetManualOffsetMs("dev-a"));
        Assert.Equal(-80, settings.GetManualOffsetMs("dev-b"));

        settings.SetManualOffsetMs("dev-a", 30);
        Assert.Equal(30, settings.GetManualOffsetMs("dev-a"));
        Assert.Equal(-80, settings.GetManualOffsetMs("dev-b"));
    }

    [Fact]
    public void AnUnknownDevice_ReadsZero_NotTheLastWrittenValue()
    {
        // 换设备后沿用旧偏移是一种会让人以为「校准丢了」的错：
        // 新设备的硬件尾段与旧设备无关，唯一诚实的初值是 0。
        var settings = new PluginSettings();
        settings.SetManualOffsetMs("dev-a", 150);

        Assert.Equal(0, settings.GetManualOffsetMs("dev-unknown"));
        Assert.Equal(0, settings.GetManualOffsetMs(null));
        Assert.Equal(0, settings.GetManualOffsetMs(string.Empty));
    }

    [Fact]
    public void ManualOffsets_SurviveJsonRoundTrip()
    {
        // 持久化是这项设置存在的意义：用耳朵调出来的值丢一次，用户就得重调一次。
        var settings = new PluginSettings();
        settings.SetManualOffsetMs("{0.0.0.00000000}.{aaaa}", 120);
        settings.SetManualOffsetMs("{0.0.0.00000000}.{bbbb}", -80);

        var json = JsonSerializer.Serialize(settings);
        var restored = JsonSerializer.Deserialize<PluginSettings>(json);

        Assert.NotNull(restored);
        Assert.Equal(120, restored.GetManualOffsetMs("{0.0.0.00000000}.{aaaa}"));
        Assert.Equal(-80, restored.GetManualOffsetMs("{0.0.0.00000000}.{bbbb}"));
    }

    [Fact]
    public void SettingAManualOffset_RaisesPropertyChanged_OnceAndOnlyOnChange()
    {
        // PropertyChanged 是两条链的共同起点：落盘（Plugin 订阅全量保存）与热生效
        // （AffectsAudioRouting → 重算对齐）。不发通知，两条链都静默断。
        var settings = new PluginSettings();
        var raised = new List<string?>();
        settings.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        settings.SetManualOffsetMs("dev-a", 120);
        Assert.Equal([nameof(PluginSettings.MediaLinkManualOffsetsMs)], raised);

        // 同值（含夹紧后同值）早退，不空发。
        settings.SetManualOffsetMs("dev-a", 120);
        Assert.Single(raised);

        // 拿不到设备标识时不写也不发——没有键可挂。
        settings.SetManualOffsetMs(null, 60);
        Assert.Single(raised);
    }
}
