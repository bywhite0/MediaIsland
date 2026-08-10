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
}
