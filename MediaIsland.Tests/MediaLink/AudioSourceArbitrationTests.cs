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
/// 而它们的全部内容就是四个布尔量的组合——那种夹具证明不了更多东西。
/// </summary>
public class AudioSourceArbitrationTests
{
    /// <summary>
    /// 是否消费上游音频。四个条件缺一不可，且都不是「帧流量」——
    /// 用连接态判断而非「最近 N 毫秒有没有收到帧」，是因为后者要加迟滞、加时间窗，
    /// 是一串补丁；更要命的是上游暂停时发的是静音帧（第 2 期保证暂停不断流），
    /// 按流量判断会误判成「上游没了」而回落本机——用户会看到本机的声音，
    /// 却以为那是远端的。
    /// </summary>
    [Theory]
    [InlineData(false, true, true, true, false)]    // 上游功能没启用
    [InlineData(true, false, true, true, false)]    // 启用了但没连上
    [InlineData(true, true, false, true, false)]    // 连上了但对端不支持 audio
    [InlineData(true, true, true, false, false)]    // 都齐了但岛上没人看
    [InlineData(true, true, true, true, true)]      // 四个条件同时成立
    public void UpstreamConsumption_RequiresAllFourConditions(
        bool upstreamEnabled, bool connected, bool supportsAudio, bool visualizationDemanded, bool expected)
    {
        Assert.Equal(
            expected,
            MediaLinkUpstreamHostedService.ShouldConsumeUpstreamAudio(
                upstreamEnabled, connected, supportsAudio, visualizationDemanded));
    }

    [Fact]
    public void UpstreamConsumption_IsPureAndStateless()
    {
        // 无状态是「不抖动」的全部依据：相同的连接态重复求值必须恒等，
        // 否则采集会在上游断续时反复启停，而每次启停都是一次音频端点的抢占与释放。
        for (var i = 0; i < 8; i++)
        {
            Assert.True(MediaLinkUpstreamHostedService.ShouldConsumeUpstreamAudio(true, true, true, true));
            Assert.False(MediaLinkUpstreamHostedService.ShouldConsumeUpstreamAudio(true, true, true, false));
        }
    }

    /// <summary>
    /// 本机采集需求。两个来源取并集，但可视化那一项要减去「正在用上游音频」。
    /// </summary>
    [Theory]
    [InlineData(false, false, false, false)]   // 谁都不要
    [InlineData(false, true, false, true)]     // 只有岛上的频谱要
    [InlineData(false, true, true, false)]     // 正在放上游的声音，本机采集是多余的
    [InlineData(true, false, false, true)]     // 只有别的客户端订了 audio
    [InlineData(true, false, true, true)]      // 协议需求不受上游影响
    [InlineData(true, true, true, true)]       // 协议需求还在，照采
    [InlineData(true, true, false, true)]      // 两个来源都要
    public void LocalCapture_UnionsProtocolAndVisualizationDemand(
        bool protocolDemand, bool visualizationDemand, bool upstreamAudioActive, bool expected)
    {
        Assert.Equal(
            expected,
            MediaLinkHostedService.ShouldCaptureLocally(
                protocolDemand, visualizationDemand, upstreamAudioActive));
    }

    [Fact]
    public void ProtocolDemand_IsIndependentOfUpstreamAudio()
    {
        // 这一格最容易写错：正在消费上游音频时，本机采集对**可视化**是多余的，
        // 但对**协议**不是——订阅 audio 的那些客户端要的是本机这台机器的声音，
        // 不是本机转发的上游声音。把它一起关掉，会让别人的频谱毫无征兆地静掉。
        Assert.True(MediaLinkHostedService.ShouldCaptureLocally(
            protocolDemand: true, visualizationDemand: false, upstreamAudioActive: true));
        Assert.True(MediaLinkHostedService.ShouldCaptureLocally(
            protocolDemand: true, visualizationDemand: true, upstreamAudioActive: true));
    }

    [Fact]
    public void LosingUpstream_ReenablesLocalCaptureWhenSomethingIsWatching()
    {
        // 上游断连 → 仲裁转假 → 本机采集自动接上，无需任何显式的「回落」代码路径。
        // 这正是把两者都写成状态函数的收益：断连只需让一个入参变假。
        Assert.False(MediaLinkHostedService.ShouldCaptureLocally(false, true, upstreamAudioActive: true));
        Assert.True(MediaLinkHostedService.ShouldCaptureLocally(false, true, upstreamAudioActive: false));
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
            upstreamEnabled: true, connected: true, supportsAudio: true,
            visualizationDemanded: demand.IsDemanded));
        Assert.False(MediaLinkHostedService.ShouldCaptureLocally(
            protocolDemand: false, visualizationDemand: demand.IsDemanded, upstreamAudioActive: false));
    }
}
