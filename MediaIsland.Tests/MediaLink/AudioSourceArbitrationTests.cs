using MediaIsland.Services.Audio.Visualization;
using MediaIsland.Services.MediaLink;
using Xunit;

namespace MediaIsland.Tests.MediaLink;

/// <summary>
/// 音源仲裁。两个判定各自是一行布尔表达式，但错了都很难查——
/// 表现是「频谱不动」或「明明连着上游却显示本机的声音」，而不是异常。
/// 故把它们从宿主服务里抽成纯函数，用真值表逐格锁死。
///
/// 抽出来还有一层意义：这两个判定横跨两个宿主服务与协议层，
/// 真要端到端驱动，得起真实 listener、真实 socket 与真实音频设备。
/// 而它们的全部内容就是三个与七个布尔量的组合——那种夹具证明不了更多东西。
/// </summary>
public class AudioSourceArbitrationTests
{
    /// <summary>
    /// 是否消费上游音频。前四个条件是「能不能」，后三个取并集是「要不要」。
    ///
    /// externalMediaEffective 是本期新增的一格。缺了它，媒体源模式设为仅本机时
    /// 仍会消费上游音频驱动频谱，于是岛上显示的是远端的频谱配本机的歌词。
    ///
    /// 判据全是状态而非帧流量：按「最近 N 毫秒有没有收到帧」来定，上游断续时
    /// 采集会反复启停，得加迟滞、加时间窗，是一串补丁；更要命的是上游暂停时发的是
    /// 静音帧，按流量判断会误判成上游没了而回落本机——用户会看到本机的声音，
    /// 却以为那是远端的。
    /// </summary>
    [Theory]
    [InlineData(false, true, true, true, true, true, true, false)]   // 上游功能没启用
    [InlineData(true, false, true, true, true, true, true, false)]   // 没连上
    [InlineData(true, true, false, true, true, true, true, false)]   // 对端不支持 audio
    [InlineData(true, true, true, false, true, true, true, false)]   // 生效媒体不是上游的
    [InlineData(true, true, true, true, false, false, false, false)] // 三个需求都没有
    [InlineData(true, true, true, true, true, false, false, true)]   // 只有可视化要
    [InlineData(true, true, true, true, false, true, false, true)]   // 只有下游转发要
    [InlineData(true, true, true, true, false, false, true, true)]   // 只有本机播放要
    [InlineData(true, true, true, true, true, true, true, true)]     // 全都要
    public void UpstreamConsumption_RequiresAllCapabilitiesAndAnyDemand(
        bool upstreamEnabled, bool connected, bool supportsAudio, bool externalMediaEffective,
        bool visualizationDemanded, bool downstreamAudioDemanded, bool playbackEnabled,
        bool expected)
    {
        Assert.Equal(
            expected,
            MediaLinkUpstreamHostedService.ShouldConsumeUpstreamAudio(
                upstreamEnabled, connected, supportsAudio, externalMediaEffective,
                visualizationDemanded, downstreamAudioDemanded, playbackEnabled));
    }

    [Fact]
    public void UpstreamConsumption_IsPureAndStateless()
    {
        // 无状态是「不抖动」的全部依据：相同输入重复求值必须恒等，
        // 否则采集会在上游断续时反复启停，而每次启停都是一次音频端点的抢占与释放。
        for (var i = 0; i < 8; i++)
        {
            Assert.True(MediaLinkUpstreamHostedService.ShouldConsumeUpstreamAudio(
                true, true, true, true, true, true, true));
            Assert.False(MediaLinkUpstreamHostedService.ShouldConsumeUpstreamAudio(
                true, true, true, true, false, false, false));
        }
    }

    /// <summary>
    /// 本机采集需求。两个需求来源取并集，再整体减去「当前生效的媒体来自上游」。
    ///
    /// 第 3 期让协议需求无条件成立，理由是订阅 audio 的客户端要的是本机的声音。
    /// 转发存在后这条不再成立：下游要的是本机认定的当前曲目的声音，而那首歌可能来自上游。
    /// 继续无条件采集会让下游收到本机的 PCM 配上游的 trackToken——校验能过但内容错配，
    /// 表现为声音和字对不上，比丢帧难查得多。
    /// </summary>
    [Theory]
    [InlineData(false, false, false, false)]  // 谁都不要
    [InlineData(false, true, false, true)]    // 只有岛上的频谱要，且媒体是本机的
    [InlineData(true, false, false, true)]    // 只有下游订阅者要，且媒体是本机的
    [InlineData(true, true, false, true)]     // 两个来源都要
    [InlineData(false, true, true, false)]    // 媒体来自上游：可视化该看上游的
    [InlineData(true, false, true, false)]    // 媒体来自上游：下游该收转发的
    [InlineData(true, true, true, false)]     // 媒体来自上游：两个来源都不采本机
    public void LocalCapture_RequiresDemandAndLocalMedia(
        bool protocolDemand, bool visualizationDemand, bool externalMediaEffective, bool expected)
    {
        Assert.Equal(
            expected,
            MediaLinkHostedService.ShouldCaptureLocally(
                protocolDemand, visualizationDemand, externalMediaEffective));
    }

    [Fact]
    public void EffectiveMediaBackToLocal_ReenablesLocalCaptureWhenSomethingIsWatching()
    {
        // 生效媒体切回本机 → 仲裁转假 → 本机采集自动接上，无需任何显式的「回落」代码路径。
        // 这正是把两者都写成状态函数的收益：上游断连只需经由仲裁让这一个入参变假。
        Assert.False(MediaLinkHostedService.ShouldCaptureLocally(false, true, externalMediaEffective: true));
        Assert.True(MediaLinkHostedService.ShouldCaptureLocally(false, true, externalMediaEffective: false));
    }

    [Fact]
    public void WithoutAnySpectrumComponent_NothingIsSubscribedOrCaptured()
    {
        // 「新功能默认不改变旧行为」这条，用真实的需求对象走一遍而不是直接传 false：
        // 需求的初值本身就是判据的一部分。它的反面（默认开启）不会有任何报错，
        // 只会让所有升级上来的用户平白多占一个音频端点、多传约 192KB/s。
        var demand = new AudioVisualizationDemand();

        Assert.False(demand.IsDemanded);
        Assert.False(MediaLinkUpstreamHostedService.ShouldConsumeUpstreamAudio(
            upstreamEnabled: true, connected: true, supportsAudio: true, externalMediaEffective: true,
            visualizationDemanded: demand.IsDemanded, downstreamAudioDemanded: false,
            playbackEnabled: false));
        Assert.False(MediaLinkHostedService.ShouldCaptureLocally(
            protocolDemand: false, visualizationDemand: demand.IsDemanded, externalMediaEffective: false));
    }
}
