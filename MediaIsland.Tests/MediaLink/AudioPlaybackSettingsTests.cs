using MediaIsland.Models;
using Xunit;

namespace MediaIsland.Tests.MediaLink;

/// <summary>
/// 抖动缓冲深度的取值。夹紧而非拒绝：设置页里的数字框允许任意输入，
/// 拒绝会让用户面对一个不生效又不报错的输入框。
/// </summary>
public class AudioPlaybackSettingsTests
{
    [Fact]
    public void PlaybackBufferMs_DefaultsTo200()
    {
        // 200ms 对 WiFi 的重传与漫游尖峰够用。有线用户可以调低。
        Assert.Equal(200, new PluginSettings().MediaLinkPlaybackBufferMs);
    }

    [Theory]
    [InlineData(0, 50)]
    [InlineData(10, 50)]
    [InlineData(50, 50)]
    [InlineData(200, 200)]
    [InlineData(1000, 1000)]
    [InlineData(5000, 1000)]
    [InlineData(-1, 50)]
    public void PlaybackBufferMs_IsClampedToSupportedRange(int input, int expected)
    {
        var settings = new PluginSettings { MediaLinkPlaybackBufferMs = input };

        Assert.Equal(expected, settings.MediaLinkPlaybackBufferMs);
    }

    [Fact]
    public void PlaybackBufferMs_ClampedWriteStillRaisesChange()
    {
        // 越界输入被夹紧后仍须发通知：设置页的数字框绑的是双向绑定，
        // 不发通知则界面继续显示用户敲进去的越界值，而生效值已是夹紧后的那个——
        // 用户看到 5000 却按 1000 播放，且没有任何提示。
        var settings = new PluginSettings();
        var changed = new List<string?>();
        settings.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        settings.MediaLinkPlaybackBufferMs = 5000;

        Assert.Contains(nameof(PluginSettings.MediaLinkPlaybackBufferMs), changed);
        Assert.Equal(1000, settings.MediaLinkPlaybackBufferMs);
    }

    [Fact]
    public void PlaybackBufferMs_SettingSameValue_RaisesNoChange()
    {
        // 幂等：存盘与 Configure 都挂在 PropertyChanged 上，
        // 同值写入发通知会让播放无故重启。
        var settings = new PluginSettings();
        var changed = new List<string?>();
        settings.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        settings.MediaLinkPlaybackBufferMs = settings.MediaLinkPlaybackBufferMs;

        Assert.DoesNotContain(nameof(PluginSettings.MediaLinkPlaybackBufferMs), changed);
    }

    [Fact]
    public void PlaybackBufferBounds_AreConsistentWithDefault()
    {
        // 默认值落在区间内，且区间非退化。这三条常量是 UI 的 Minimum/Maximum
        // 与 setter 的夹紧共同的真值源，写错任何一个都会让两处不一致。
        Assert.True(PluginSettings.MinPlaybackBufferMs < PluginSettings.MaxPlaybackBufferMs);
        Assert.InRange(
            PluginSettings.DefaultPlaybackBufferMs,
            PluginSettings.MinPlaybackBufferMs,
            PluginSettings.MaxPlaybackBufferMs);
    }

    [Fact]
    public void PlaybackIsEnabled_DefaultsToOff()
    {
        // 多数用户只看频谱，出声应是显式选择。
        Assert.False(new PluginSettings().MediaLinkPlaybackIsEnabled);
    }
}
