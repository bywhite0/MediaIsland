using System.Text.Json;
using MediaIsland.Components;
using MediaIsland.Helpers;
using Xunit;

namespace MediaIsland.Tests.Components;

/// <summary>
/// 覆盖 Issue #33：「正在播放」与「正在播放(简)」的「显示状态文本」选项。
/// 两个组件共用同一套规则，配置类逐项对齐验证，重点是默认值不改变旧行为（无媒体时整块隐藏）。
/// </summary>
public class NowPlayingStatusTextTests
{
    [Fact]
    public void NowPlaying_IsShowStatusText_DefaultsToOff()
    {
        Assert.False(new NowPlayingComponentConfig().IsShowStatusText);
    }

    [Fact]
    public void SimplyNowPlaying_IsShowStatusText_DefaultsToOff()
    {
        Assert.False(new SimplyNowPlayingComponentConfig().IsShowStatusText);
    }

    /// <summary>
    /// 旧配置文件里没有该键，反序列化后必须保持关闭，否则已有用户的岛上会突然多出文字。
    /// </summary>
    [Fact]
    public void LegacyConfigsWithoutKey_StayOff()
    {
        const string legacyJson = """{"IsHideWhenPaused":true,"IsShowPlaybackStatus":true}""";

        var nowPlaying = JsonSerializer.Deserialize<NowPlayingComponentConfig>(legacyJson);
        var simply = JsonSerializer.Deserialize<SimplyNowPlayingComponentConfig>(legacyJson);

        Assert.NotNull(nowPlaying);
        Assert.NotNull(simply);
        Assert.False(nowPlaying!.IsShowStatusText);
        Assert.False(simply!.IsShowStatusText);
        Assert.True(nowPlaying.IsHideWhenPaused);
        Assert.True(simply.IsHideWhenPaused);
    }

    [Fact]
    public void NowPlaying_IsShowStatusText_Persists()
    {
        var json = JsonSerializer.Serialize(new NowPlayingComponentConfig { IsShowStatusText = true });
        Assert.Contains("\"IsShowStatusText\":true", json);

        var restored = JsonSerializer.Deserialize<NowPlayingComponentConfig>(json);
        Assert.True(restored!.IsShowStatusText);
    }

    [Fact]
    public void SimplyNowPlaying_IsShowStatusText_Persists()
    {
        var json = JsonSerializer.Serialize(new SimplyNowPlayingComponentConfig { IsShowStatusText = true });
        Assert.Contains("\"IsShowStatusText\":true", json);

        var restored = JsonSerializer.Deserialize<SimplyNowPlayingComponentConfig>(json);
        Assert.True(restored!.IsShowStatusText);
    }

    /// <summary>
    /// 组件靠 PropertyChanged 即时刷新；只在真实变化时通知，开—关—关 应恰好两次。
    /// </summary>
    [Fact]
    public void NowPlaying_IsShowStatusText_NotifiesOnlyOnRealChange()
    {
        var settings = new NowPlayingComponentConfig();
        var changed = new List<string?>();
        settings.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        settings.IsShowStatusText = true;
        settings.IsShowStatusText = true;
        settings.IsShowStatusText = false;

        Assert.Equal(
            [nameof(NowPlayingComponentConfig.IsShowStatusText), nameof(NowPlayingComponentConfig.IsShowStatusText)],
            changed);
    }

    [Fact]
    public void SimplyNowPlaying_IsShowStatusText_NotifiesOnlyOnRealChange()
    {
        var settings = new SimplyNowPlayingComponentConfig();
        var changed = new List<string?>();
        settings.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        settings.IsShowStatusText = true;
        settings.IsShowStatusText = true;
        settings.IsShowStatusText = false;

        Assert.Equal(
            [nameof(SimplyNowPlayingComponentConfig.IsShowStatusText), nameof(SimplyNowPlayingComponentConfig.IsShowStatusText)],
            changed);
    }

    [Theory]
    [InlineData(true, true, true)]    // 无可显示媒体 + 开启：显示「未在播放」
    [InlineData(true, false, false)]  // 无可显示媒体 + 关闭：旧行为，整块隐藏
    [InlineData(false, true, false)]  // 有媒体（含「暂停时隐藏」触发的主动隐藏）：不补状态文本
    [InlineData(false, false, false)]
    public void IsVisible_OnlyWhenIdleAndEnabled(bool isIdle, bool isShowStatusText, bool expected)
    {
        Assert.Equal(expected, NowPlayingStatusText.IsVisible(isIdle, isShowStatusText));
    }

    [Fact]
    public void IdleText_IsUnified()
    {
        Assert.Equal("未在播放", NowPlayingStatusText.Idle);
    }
}
