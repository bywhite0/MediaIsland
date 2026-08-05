using System.Text.Json;
using MediaIsland.Components;
using Xunit;

namespace MediaIsland.Tests.Components;

/// <summary>
/// 覆盖 Issue #37 新增的「内容最大宽度 / 超长时滚动」配置。
/// 三个组件的配置类结构相同，因此逐项对齐验证，重点是默认值不改变旧行为。
/// </summary>
public class MaxContentWidthConfigTests
{
    [Fact]
    public void NowPlaying_MaxContentWidth_DefaultsToUnlimited()
    {
        var settings = new NowPlayingComponentConfig();

        Assert.Equal(0, settings.MaxContentWidth);
        Assert.False(settings.IsScrollWhenOverflow);
    }

    [Fact]
    public void SimplyNowPlaying_MaxContentWidth_DefaultsToUnlimited()
    {
        var settings = new SimplyNowPlayingComponentConfig();

        Assert.Equal(0, settings.MaxContentWidth);
        Assert.False(settings.IsScrollWhenOverflow);
    }

    [Fact]
    public void Lyrics_MaxContentWidth_DefaultsToUnlimited()
    {
        var settings = new LyricsComponentConfig();

        Assert.Equal(0, settings.MaxContentWidth);
        Assert.False(settings.IsScrollWhenOverflow);
    }

    [Theory]
    [InlineData(0, 0)]                          // 显式关闭
    [InlineData(-50, 0)]                        // 负值视为不限制
    [InlineData(20, 40)]                        // 低于下限被抬升，避免宽度小到无法阅读
    [InlineData(240, 240)]                      // 常规取值
    [InlineData(5000, 1000)]                    // 高于上限被压回
    [InlineData(double.NaN, 0)]
    [InlineData(double.PositiveInfinity, 0)]
    public void NowPlaying_MaxContentWidth_NormalizesToSupportedRange(double value, double expected)
    {
        var settings = new NowPlayingComponentConfig { MaxContentWidth = value };

        Assert.Equal(expected, settings.MaxContentWidth);
    }

    [Theory]
    [InlineData(-50, 0)]
    [InlineData(20, 40)]
    [InlineData(240, 240)]
    [InlineData(5000, 1000)]
    [InlineData(double.NaN, 0)]
    public void SimplyNowPlaying_MaxContentWidth_NormalizesToSupportedRange(double value, double expected)
    {
        var settings = new SimplyNowPlayingComponentConfig { MaxContentWidth = value };

        Assert.Equal(expected, settings.MaxContentWidth);
    }

    [Theory]
    [InlineData(-50, 0)]
    [InlineData(20, 40)]
    [InlineData(240, 240)]
    [InlineData(5000, 1000)]
    [InlineData(double.NaN, 0)]
    public void Lyrics_MaxContentWidth_NormalizesToSupportedRange(double value, double expected)
    {
        var settings = new LyricsComponentConfig { MaxContentWidth = value };

        Assert.Equal(expected, settings.MaxContentWidth);
    }

    [Fact]
    public void NowPlaying_OverflowSettings_Persist()
    {
        var settings = new NowPlayingComponentConfig
        {
            MaxContentWidth = 240,
            IsScrollWhenOverflow = true
        };

        var json = JsonSerializer.Serialize(settings);
        Assert.Contains("\"MaxContentWidth\":240", json);
        Assert.Contains("\"IsScrollWhenOverflow\":true", json);

        var restored = JsonSerializer.Deserialize<NowPlayingComponentConfig>(json);
        Assert.NotNull(restored);
        Assert.Equal(240, restored!.MaxContentWidth);
        Assert.True(restored.IsScrollWhenOverflow);
    }

    [Fact]
    public void SimplyNowPlaying_OverflowSettings_Persist()
    {
        var settings = new SimplyNowPlayingComponentConfig
        {
            MaxContentWidth = 240,
            IsScrollWhenOverflow = true
        };

        var json = JsonSerializer.Serialize(settings);
        Assert.Contains("\"MaxContentWidth\":240", json);
        Assert.Contains("\"IsScrollWhenOverflow\":true", json);

        var restored = JsonSerializer.Deserialize<SimplyNowPlayingComponentConfig>(json);
        Assert.NotNull(restored);
        Assert.Equal(240, restored!.MaxContentWidth);
        Assert.True(restored.IsScrollWhenOverflow);
    }

    [Fact]
    public void Lyrics_OverflowSettings_Persist()
    {
        var settings = new LyricsComponentConfig
        {
            MaxContentWidth = 240,
            IsScrollWhenOverflow = true
        };

        var json = JsonSerializer.Serialize(settings);
        Assert.Contains("\"MaxContentWidth\":240", json);
        Assert.Contains("\"IsScrollWhenOverflow\":true", json);

        var restored = JsonSerializer.Deserialize<LyricsComponentConfig>(json);
        Assert.NotNull(restored);
        Assert.Equal(240, restored!.MaxContentWidth);
        Assert.True(restored.IsScrollWhenOverflow);
    }

    /// <summary>
    /// 旧配置文件里没有这两个键，反序列化后必须回到「不限制」，否则会改变已有用户的布局。
    /// </summary>
    [Fact]
    public void LegacyConfigWithoutOverflowKeys_StaysUnlimited()
    {
        const string legacyJson = """{"IsShowAlbumArt":true,"IsLeftNegativeMargin":true}""";

        var restored = JsonSerializer.Deserialize<NowPlayingComponentConfig>(legacyJson);

        Assert.NotNull(restored);
        Assert.Equal(0, restored!.MaxContentWidth);
        Assert.False(restored.IsScrollWhenOverflow);
        Assert.True(restored.IsLeftNegativeMargin);
    }

    [Fact]
    public void MaxContentWidth_RaisesChangeNotificationOnlyOnRealChange()
    {
        var settings = new NowPlayingComponentConfig();
        var changed = new List<string?>();
        settings.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        settings.MaxContentWidth = 240;
        settings.MaxContentWidth = 240;

        Assert.Equal([nameof(NowPlayingComponentConfig.MaxContentWidth)], changed);
    }

    [Fact]
    public void IsScrollWhenOverflow_RaisesChangeNotificationOnlyOnRealChange()
    {
        var settings = new SimplyNowPlayingComponentConfig();
        var changed = new List<string?>();
        settings.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        settings.IsScrollWhenOverflow = true;
        settings.IsScrollWhenOverflow = true;

        Assert.Equal([nameof(SimplyNowPlayingComponentConfig.IsScrollWhenOverflow)], changed);
    }
}
