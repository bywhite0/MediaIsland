using MediaIsland.Models;
using MediaIsland.Services.Audio.Playback;

namespace MediaIsland.Services.MediaLink;

/// <summary>
/// 三个音频角色（本机采集、向上游订阅、向下游转发）的归属由两个宿主服务各自重算，
/// 但触发重算的信号来自对方与媒体源仲裁器。这些边只能接在两者之外——
/// 互相注入会形成构造期循环依赖。
///
/// 抽成方法而不是留在 DI 回调里，是因为 DI 回调无法被测试驱动。少接一条边不会有
/// 任何报错，只会让某个角色在某种状态组合下静默失效，靠读代码发现不了；
/// 而这三条边各自对应一类状态变化，缺哪条都得有测试能立刻转红。
/// </summary>
public static class MediaLinkAudioWiring
{
    /// <summary>
    /// 接上三条重算边。幂等性由两侧的重算方法保证（都是读当前状态而非应用增量），
    /// 故重复触发只是多算一次，不会累积出错误状态。
    /// </summary>
    public static void Connect(
        MediaLinkUpstreamHostedService upstream,
        MediaLinkHostedService server,
        MediaSourceCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(upstream);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(coordinator);

        // 音源仲裁变了就让服务端重算本机采集需求。
        upstream.AudioSourceChanged += (_, _) => _ = server.RecomputeAudioCaptureDemandAsync();

        // 生效媒体来源变了，三个角色的归属就变了：本机采集、上游转发、本机播放。
        // 两个宿主服务各自重算，不互相调用。
        coordinator.EffectiveMediaChanged += (_, _) =>
        {
            upstream.RecomputeAudioSubscription();
            _ = server.RecomputeAudioCaptureDemandAsync();
        };

        // 下游订阅 audio 是「要不要转发」的唯一需求来源，而它只在这里变化。
        // 缺这条边时，上游暂停（媒体快照相等，协调器不发生效媒体变化）
        // 且岛上无频谱组件，下游订阅 audio 就拉不起上游订阅，转发不会开始。
        //
        // 这条边不会与第一条形成自激循环：需求变化是边沿触发的，
        // 回来的那次重算读到同一个值，不再发出通知。
        server.DownstreamAudioDemandChanged += (_, _) => upstream.RecomputeAudioSubscription();
    }

    /// <summary>
    /// 接上播放的重算边。与 <see cref="Connect"/> 分开是为了保住那个方法「内部无分支」
    /// 的性质——注册测试正是靠它才敢说「一条通即三条都接上了」。
    ///
    /// 只需要一条边：播放开关与缓冲深度的变化都由上游服务转成
    /// <see cref="MediaLinkUpstreamHostedService.AudioSourceChanged"/>，
    /// 仲裁与连接态的变化本来就走这个信号。
    /// </summary>
    public static void ConnectPlayback(
        MediaLinkUpstreamHostedService upstream,
        AudioPlaybackService playback,
        Func<PluginSettings> settingsAccessor)
    {
        ArgumentNullException.ThrowIfNull(upstream);
        ArgumentNullException.ThrowIfNull(playback);
        ArgumentNullException.ThrowIfNull(settingsAccessor);

        upstream.AudioSourceChanged += (_, _) =>
        {
            var (enabled, targetBufferMs) =
                ResolvePlayback(settingsAccessor(), upstream.IsConsumingUpstreamAudio);
            playback.Configure(enabled, targetBufferMs);
        };
    }

    /// <summary>
    /// 由设置与仲裁结果算出播放配置。抽成纯函数：这两个条件的合成方式一旦写错，
    /// 表现是「装了频谱组件就自动出声」或「开了播放却不出声」，两者都不报错。
    ///
    /// 用户开了播放**且**当前真的在消费上游音频，才播。少了后一项，仲裁说该用本机媒体时
    /// 也会打开播放器，那只是占着音频端点空转，独占型音频软件会因此拿不到设备。
    /// 少了前一项，<see cref="MediaLinkUpstreamHostedService.IsConsumingUpstreamAudio"/>
    /// 会因为岛上有频谱组件而为真，于是装个频谱组件就等于自动出声。
    ///
    /// 深度在关闭态也照传：<see cref="AudioPlaybackService.Configure"/> 的幂等判据比的是
    /// 请求值，关闭态不记准的话「先调深度再开播」会用上一次的深度起播。
    /// </summary>
    internal static (bool Enabled, int TargetBufferMs) ResolvePlayback(
        PluginSettings settings,
        bool consumingUpstreamAudio) =>
        (settings.MediaLinkPlaybackIsEnabled && consumingUpstreamAudio,
            settings.MediaLinkPlaybackBufferMs);
}
