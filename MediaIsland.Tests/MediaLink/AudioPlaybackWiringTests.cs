using MediaIsland.Models;
using MediaIsland.Services.MediaLink;
using Xunit;

namespace MediaIsland.Tests.MediaLink;

/// <summary>
/// 播放的两条接线判定。两者都属于「漏一条不会报错，只会让开关看起来生效了却没效果」
/// 的那类缺陷，故各自抽成纯函数并逐格锁死。
/// </summary>
public class AudioPlaybackWiringTests
{
    [Theory]
    [InlineData(nameof(PluginSettings.MediaLinkPlaybackIsEnabled), true)]
    [InlineData(nameof(PluginSettings.MediaLinkPlaybackBufferMs), true)]
    [InlineData(nameof(PluginSettings.MediaLinkUpstreamIsEnabled), false)]
    [InlineData(nameof(PluginSettings.MediaLinkUpstreamEndpoint), false)]
    [InlineData(nameof(PluginSettings.MediaLinkUpstreamToken), false)]
    [InlineData(nameof(PluginSettings.IsTodayEatSentry), false)]
    [InlineData(null, false)]
    public void AffectsAudioRouting_CoversExactlyThePlaybackProperties(string? property, bool expected)
    {
        // 播放开关参与「要不要消费上游音频」的仲裁。漏掉它的后果是：打开播放后
        // 上游从不订阅 audio，renderer 起来了却收不到帧——开关开着但没声音，
        // 且与「对方没在放」无从区分。
        //
        // 地址与密钥必须为假：它们走防抖重连，逐字输入会产生大量无效中间态。
        Assert.Equal(
            expected,
            MediaLinkUpstreamHostedService.AffectsAudioRouting(property));
    }

    [Fact]
    public void ResolvePlayback_PlaysOnlyWhenEnabledAndConsumingUpstream()
    {
        var settings = new PluginSettings { MediaLinkPlaybackIsEnabled = true, MediaLinkPlaybackBufferMs = 120 };

        var resolved = MediaLinkAudioWiring.ResolvePlayback(settings, consumingUpstreamAudio: true);

        Assert.True(resolved.Enabled);
        Assert.Equal(120, resolved.TargetBufferMs);
    }

    [Fact]
    public void ResolvePlayback_NotConsumingUpstream_DoesNotPlay()
    {
        // 仲裁说该用本机媒体时打开 renderer 只会占着音频端点空转，
        // 而独占型音频软件会因此拿不到设备。
        var settings = new PluginSettings { MediaLinkPlaybackIsEnabled = true };

        var resolved = MediaLinkAudioWiring.ResolvePlayback(settings, consumingUpstreamAudio: false);

        Assert.False(resolved.Enabled);
    }

    [Fact]
    public void ResolvePlayback_SwitchOff_DoesNotPlayEvenWhileConsuming()
    {
        // 岛上有频谱组件时上游音频照样在消费，但用户没要求出声。
        // 少了这一项，装了频谱组件就等于自动出声。
        var settings = new PluginSettings { MediaLinkPlaybackIsEnabled = false };

        var resolved = MediaLinkAudioWiring.ResolvePlayback(settings, consumingUpstreamAudio: true);

        Assert.False(resolved.Enabled);
    }

    [Fact]
    public void ResolvePlayback_PassesDepthThroughEvenWhenDisabled()
    {
        // 深度照传：Configure 的幂等判据比的是请求值，深度在关闭态也要记准，
        // 否则「先调深度再开播」会用上一次的深度起播。
        var settings = new PluginSettings { MediaLinkPlaybackIsEnabled = false, MediaLinkPlaybackBufferMs = 500 };

        var resolved = MediaLinkAudioWiring.ResolvePlayback(settings, consumingUpstreamAudio: false);

        Assert.Equal(500, resolved.TargetBufferMs);
    }
}
