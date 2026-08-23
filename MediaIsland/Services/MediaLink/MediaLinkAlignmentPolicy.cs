namespace MediaIsland.Services.MediaLink;

/// <summary>
/// 为什么没对齐。三种原因的排查方向完全不同，故它们必须可分——合成一条
/// 「对齐不可用」的消息，用户就无从知道该去检查服务端版本、网络、还是自己的设备。
/// </summary>
internal enum MediaLinkAlignmentState
{
    /// <summary>正在对齐。</summary>
    Aligned,

    /// <summary>服务端不支持跨机对时。换服务端或升级它。</summary>
    NoCapability,

    /// <summary>服务端支持，但没声明播放延迟预算。它的配置不全。</summary>
    NoBudgetDeclared,

    /// <summary>预算减去本机设备延迟后不足最小抖动缓冲。本机设备太慢，或预算配得太小。</summary>
    BudgetTooSmall,

    /// <summary>还没对上时钟，或对端失联。等一会儿或查网络。</summary>
    NoClockOffset
}

/// <summary>一次对齐判定的结果。</summary>
internal readonly record struct MediaLinkAlignmentDecision(
    MediaLinkAlignmentState State,
    string Reason)
{
    public bool IsAligned => State == MediaLinkAlignmentState.Aligned;
}

/// <summary>
/// 判定本机此刻能不能对齐播放。纯函数，不持状态、不读时钟、不碰设备。
///
/// 抽成独立类型的理由与 native 侧把纯逻辑放在 cfg 门之外相同：这个判断有四条互相
/// 排斥的出路，而它们的差别只在归因上——四条路都让声音照常出，判据若只看「有没有对齐」
/// 就分不出实现走的是哪一条。
/// </summary>
internal static class MediaLinkAlignmentPolicy
{
    /// <summary>
    /// 判定顺序是刻意的：先协议层（对端有没有这个能力、有没有给出参数），
    /// 再本机的静态可行性（预算够不够装下最小缓冲），最后才是运行时状态（时钟对上了没）。
    ///
    /// 把运行时状态放最后，是因为它是唯一会自己好转的一条。若把它排在前面，
    /// 一台预算根本不够的机器在刚连上的那几秒会报「还没对上时钟」，
    /// 而那条提示指向的是等待，用户就会一直等。
    /// </summary>
    /// <param name="serverSupportsAudioClock">对端的 capabilities 里有没有 audio.clock。</param>
    /// <param name="declaredBudgetMs">对端声明的播放延迟预算。null 表示没声明。</param>
    /// <param name="deviceLatencyMs">本机设备取走数据之后到出声那段的估计。</param>
    /// <param name="minTargetMs">抖动缓冲的最小目标深度，即本机延迟的下限。</param>
    /// <param name="clockOffsetAvailable">跨机 offset 此刻可用。</param>
    public static MediaLinkAlignmentDecision Decide(
        bool serverSupportsAudioClock,
        long? declaredBudgetMs,
        double deviceLatencyMs,
        int minTargetMs,
        bool clockOffsetAvailable)
    {
        if (!serverSupportsAudioClock)
        {
            return new MediaLinkAlignmentDecision(
                MediaLinkAlignmentState.NoCapability,
                "服务端不支持跨机对时，已按不对齐播放");
        }

        if (declaredBudgetMs is not { } budgetMs)
        {
            // 不回落到默认值：两端各自默认成同一个数看起来一致，实则是两份各自为真的
            // 声明，改了一端就静默失配，而症状是两台机器差一个固定的量、像硬件延迟。
            return new MediaLinkAlignmentDecision(
                MediaLinkAlignmentState.NoBudgetDeclared,
                "服务端未声明播放延迟预算，已按不对齐播放");
        }

        // 本机最小可达延迟 = 设备尾段延迟 + 最小抖动缓冲深度。预算装不下它就对不齐，
        // 此时不假装对齐——假装的表现是缓冲被压到下限后误差永久为正，而外环撞着边界
        // 反复告警，看起来像控制律坏了。
        var headroomMs = budgetMs - deviceLatencyMs;
        if (headroomMs < minTargetMs)
        {
            return new MediaLinkAlignmentDecision(
                MediaLinkAlignmentState.BudgetTooSmall,
                $"播放延迟预算 {budgetMs}ms 减去本机设备延迟 {deviceLatencyMs:F1}ms 后"
                + $"不足最小缓冲 {minTargetMs}ms，已按不对齐播放");
        }

        if (!clockOffsetAvailable)
        {
            return new MediaLinkAlignmentDecision(
                MediaLinkAlignmentState.NoClockOffset,
                "尚未与服务端对上时钟，暂按不对齐播放");
        }

        return new MediaLinkAlignmentDecision(MediaLinkAlignmentState.Aligned, "正在对齐播放");
    }
}
