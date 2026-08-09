namespace MediaIsland.Controls;

/// <summary>
/// 原始幅度 → 平滑幅度。
///
/// attack 立即、decay 渐进：能量上升要跟得上鼓点，回落要有余韵；
/// 两边都平滑会让频谱迟钝，两边都不平滑会让它抖成噪声。
///
/// 放在渲染层而不在分析层，是因为分析昂贵且与观察者无关，衰减廉价且属于观察者——
/// 若把它放进共享快照，两个频谱组件就被迫共用同一套灵敏度，
/// 而灵敏度恰恰是纯粹的个人手感。
/// </summary>
public static class SpectrumEnvelope
{
    /// <summary>
    /// 帧流中断多久后视为无信号。分析器在写位置未推进时返回缓存快照，
    /// 若渲染层据此跳过更新，最后一帧的频谱会永远停在屏幕上——
    /// 这与「静音时跳过发送会让接收端 FFT 冻结在最后一帧波形上」是同一个失效形态，
    /// 只是成因从「不发帧」换成了「不刷新」。故超时即把目标拉到零，让包络自然落下。
    ///
    /// 取值要大于一帧的间隔（约 16ms）才不会把正常抖动误判成中断，
    /// 又要小到用户察觉不出延迟。200ms 是这两个约束之间的取值。
    /// </summary>
    public static readonly TimeSpan StallTimeout = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// 推进一格。上升立即到位，下降按 <paramref name="decayPerSecond"/> 渐进，
    /// 且不穿过目标值——穿过去会让频谱在回落末端抖动。
    /// </summary>
    public static float Advance(float previous, float raw, double decayPerSecond, double deltaSeconds)
    {
        if (raw >= previous)
        {
            return raw;
        }

        var decayed = previous - (float)(decayPerSecond * deltaSeconds);
        return Math.Max(Math.Max(decayed, raw), 0f);
    }

    /// <summary>
    /// 逐段推进。<paramref name="previous"/> 与 <paramref name="targets"/> 长度不一致时
    /// 以后者为准——频段数可在运行时改，长度不匹配是正常状态而非错误。
    /// 新增位置从 0 起算而不是复用末位：复用会让新增的高频段凭空带上低频段的能量。
    /// </summary>
    public static float[] AdvanceAll(
        IReadOnlyList<float> previous,
        IReadOnlyList<float> targets,
        double decayPerSecond,
        double deltaSeconds)
    {
        var result = new float[targets.Count];
        for (var i = 0; i < targets.Count; i++)
        {
            var prior = i < previous.Count ? previous[i] : 0f;
            result[i] = Advance(prior, targets[i], decayPerSecond, deltaSeconds);
        }

        return result;
    }

    /// <summary>
    /// 帧流是否已停。停了就该把目标当成零，而不是沿用最后一份快照。
    /// </summary>
    public static bool IsStalled(TimeSpan sinceLastData) => sinceLastData >= StallTimeout;
}
