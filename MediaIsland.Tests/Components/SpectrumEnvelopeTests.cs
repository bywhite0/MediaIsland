using MediaIsland.Controls;
using Xunit;

namespace MediaIsland.Tests.Components;

/// <summary>
/// 原始幅度 → 平滑幅度。attack 立即、decay 渐进：能量上升要跟得上鼓点，
/// 回落要有余韵；两边都平滑会让频谱迟钝，两边都不平滑会让它抖成噪声。
///
/// 这一层最关键的不是平滑本身，而是 <see cref="SpectrumEnvelope.IsStalled"/>——
/// 分析器在没有新数据时返回缓存快照，渲染层若据此跳过更新，最后一帧的频谱会
/// 永远停在屏幕上。那与「静音时跳过发送会让接收端 FFT 冻结在最后一帧波形上」
/// 是同一个失效形态，只是成因从「不发帧」换成了「不刷新」。
/// </summary>
public class SpectrumEnvelopeTests
{
    [Fact]
    public void RisingValue_TakesEffectImmediately()
    {
        // attack 不平滑：鼓点起来时频谱要立刻跟上，慢一帧就能看出拖沓。
        Assert.Equal(1f, SpectrumEnvelope.Advance(previous: 0f, raw: 1f, decayPerSecond: 2, deltaSeconds: 0.016));
        Assert.Equal(1f, SpectrumEnvelope.Advance(previous: 0f, raw: 1f, decayPerSecond: 2, deltaSeconds: 10));
    }

    [Fact]
    public void SlightlyRisingValue_AlsoTakesTheRawValue()
    {
        Assert.Equal(0.6f, SpectrumEnvelope.Advance(previous: 0.5f, raw: 0.6f, decayPerSecond: 2, deltaSeconds: 0.1));
    }

    [Fact]
    public void FallingValue_DecaysAtTheConfiguredRate()
    {
        // 每秒衰减 2，走 0.1 秒即衰减 0.2。
        Assert.Equal(
            0.8f,
            SpectrumEnvelope.Advance(previous: 1f, raw: 0f, decayPerSecond: 2, deltaSeconds: 0.1),
            1e-5);
    }

    [Fact]
    public void FallingValue_NeverUndershootsTheRawValue()
    {
        // 衰减量大于差值时停在 raw 上，不会穿过去——穿过去会让频谱在回落末端抖动。
        Assert.Equal(
            0.4f,
            SpectrumEnvelope.Advance(previous: 0.5f, raw: 0.4f, decayPerSecond: 10, deltaSeconds: 1),
            1e-5);
    }

    [Fact]
    public void FallingValue_DecaysGraduallyWhenTheStepIsSmall()
    {
        var result = SpectrumEnvelope.Advance(previous: 0.5f, raw: 0.4f, decayPerSecond: 0.2, deltaSeconds: 0.1);

        // 每秒 0.2、走 0.1 秒即 0.02，落到 0.48——不该一步跳到 0.4。
        Assert.InRange(result, 0.4f, 0.5f);
        Assert.NotEqual(0.4f, result);
    }

    [Fact]
    public void RepeatedDecay_ConvergesToZeroAndNeverGoesNegative()
    {
        var value = 1f;
        for (var i = 0; i < 200; i++)
        {
            value = SpectrumEnvelope.Advance(value, raw: 0f, decayPerSecond: 2, deltaSeconds: 0.016);
            Assert.True(value >= 0f, $"第 {i} 步出现负值：{value}");
        }

        Assert.Equal(0f, value);
    }

    [Fact]
    public void ZeroDelta_DoesNotAdvance()
    {
        // 定时器可能在同一毫秒内触发两次；此时不该有任何推进。
        Assert.Equal(0.7f, SpectrumEnvelope.Advance(previous: 0.7f, raw: 0f, decayPerSecond: 2, deltaSeconds: 0));
    }

    // ---- 数组版 ----

    [Fact]
    public void AdvanceAll_FollowsTheTargetLength()
    {
        // 频段数可在运行时改，长度不匹配是正常状态而非错误。
        var result = SpectrumEnvelope.AdvanceAll([1f, 1f, 1f, 1f], [0f, 0f], decayPerSecond: 2, deltaSeconds: 0.1);

        Assert.Equal(2, result.Length);
    }

    [Fact]
    public void AdvanceAll_NewPositionsStartFromZero()
    {
        // 目标比上次长时，新位置没有历史值。从 0 起算而不是读越界或复用末位——
        // 复用末位会让新增的高频段凭空带上低频段的能量。
        var result = SpectrumEnvelope.AdvanceAll([0.5f], [0.2f, 0.2f, 0.2f], decayPerSecond: 2, deltaSeconds: 0.1);

        Assert.Equal(3, result.Length);
        Assert.Equal(0.2f, result[1]);
        Assert.Equal(0.2f, result[2]);
    }

    [Fact]
    public void AdvanceAll_AppliesTheSameRuleAsAdvance()
    {
        var result = SpectrumEnvelope.AdvanceAll(
            [1f, 0f], [0f, 1f], decayPerSecond: 2, deltaSeconds: 0.1);

        Assert.Equal(0.8f, result[0], 1e-5);   // 下降：渐进
        Assert.Equal(1f, result[1]);            // 上升：立即
    }

    [Fact]
    public void AdvanceAll_EmptyTarget_ReturnsEmpty()
    {
        Assert.Empty(SpectrumEnvelope.AdvanceAll([1f, 1f], [], decayPerSecond: 2, deltaSeconds: 0.1));
    }

    // ---- 帧流中断 ----

    [Fact]
    public void FreshData_IsNotStalled()
    {
        Assert.False(SpectrumEnvelope.IsStalled(TimeSpan.Zero));
        Assert.False(SpectrumEnvelope.IsStalled(SpectrumEnvelope.StallTimeout - TimeSpan.FromMilliseconds(1)));
    }

    [Fact]
    public void DataOlderThanTheTimeout_IsStalled()
    {
        // 超时即把目标拉到零，让包络自然落下——不这么做的话，上游断连后
        // 最后一帧的频谱会永远停在屏幕上，而用户会以为那首歌还在放。
        Assert.True(SpectrumEnvelope.IsStalled(SpectrumEnvelope.StallTimeout));
        Assert.True(SpectrumEnvelope.IsStalled(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void StallTimeout_IsLongerThanASingleFrameButShorterThanNoticeable()
    {
        // 阈值要大于一帧的间隔（约 16ms）才不会把正常抖动误判为中断，
        // 又要小到用户察觉不出延迟。200ms 是这两个约束之间的取值。
        Assert.True(SpectrumEnvelope.StallTimeout > TimeSpan.FromMilliseconds(50));
        Assert.True(SpectrumEnvelope.StallTimeout <= TimeSpan.FromMilliseconds(500));
    }
}
