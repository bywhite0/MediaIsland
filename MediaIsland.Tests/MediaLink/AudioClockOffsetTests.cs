using MediaIsland.Services.MediaLink;
using Xunit;

namespace MediaIsland.Tests.MediaLink;

public class AudioClockOffsetTests
{
    private const long Ms = TimeSpan.TicksPerMillisecond;

    /// <summary>
    /// 造一个样本：服务端时钟比客户端快 offset，单程去 outboundMs、回 inboundMs，
    /// 服务端处理耗 processingMs。这样构造而不是直接填四个数，是为了让每条判据
    /// 说的是「已知真值能否被还原」，而不是「这四个数算出来等于那个数」。
    /// </summary>
    private static AudioClockSample Sample(
        double offsetMs, double outboundMs, double inboundMs, double processingMs = 0)
    {
        var t1 = 0L;
        var t2 = (long)((outboundMs + offsetMs) * Ms);
        var t3 = t2 + (long)(processingMs * Ms);
        var t4 = (long)((outboundMs + processingMs + inboundMs) * Ms);
        return new AudioClockSample(t1, t2, t3, t4);
    }

    [Fact]
    public void Tick_IsTheSameUnitAsTimeSpanTicks()
    {
        // 协议把单位定成 100ns tick，理由之一是它与 TimeSpan.Ticks 一一对应，
        // 托管侧零换算。这条判据钉的是那个「一一对应」，不是某个字面量——
        // 若哪天有人把单位改成微秒，这里必红。
        Assert.Equal(10_000_000L, TimeSpan.TicksPerSecond);
        Assert.Equal(500_000L, AudioClockOffsetEstimator.MaxAcceptableRoundTrip.Ticks);
    }

    [Fact]
    public void SymmetricPath_RecoversTheOffsetExactly()
    {
        var sample = Sample(offsetMs: 40, outboundMs: 3, inboundMs: 3);

        Assert.Equal(40 * Ms, sample.OffsetTicks);
        Assert.Equal(6 * Ms, sample.RoundTripTicks);
    }

    [Fact]
    public void ServerProcessingTime_IsExcludedFromRoundTrip()
    {
        // t2 与 t3 存在的全部理由。处理耗时若被算进往返，RTT 变大，而 offset
        // 也会被这段时间带偏——两个量同时错，且错的方向一致，最难发现。
        var sample = Sample(offsetMs: 40, outboundMs: 3, inboundMs: 3, processingMs: 20);

        Assert.Equal(6 * Ms, sample.RoundTripTicks);
        Assert.Equal(40 * Ms, sample.OffsetTicks);
    }

    [Fact]
    public void AsymmetricPath_ErrorIsBoundedByHalfTheRoundTrip()
    {
        // 不对称是 offset 误差的唯一来源，其上界为 RTT/2。这条判据把那个上界
        // 钉成可断言的东西：去 8 回 2，真值 40，估计值偏离不超过 5。
        var sample = Sample(offsetMs: 40, outboundMs: 8, inboundMs: 2);

        var error = Math.Abs(sample.OffsetTicks - 40 * Ms);
        Assert.True(error <= sample.RoundTripTicks / 2, $"误差 {error} 超出 RTT/2 {sample.RoundTripTicks / 2}");
        Assert.Equal(3 * Ms, error);
    }

    [Fact]
    public void EmptyWindow_ReportsUnavailable()
    {
        var estimator = new AudioClockOffsetEstimator();

        Assert.False(estimator.TryGetOffset(out _, out _));
    }

    [Fact]
    public void NegativeRoundTrip_IsRejectedRatherThanClamped()
    {
        // 物理上不可能为负。夹紧到零会让这个坏样本成为「RTT 最小」的那一个，
        // 于是它必然被选中——一个错值挤掉了所有好值。
        var estimator = new AudioClockOffsetEstimator();
        var broken = new AudioClockSample(T1: 0, T2: 100 * Ms, T3: 900 * Ms, T4: 500 * Ms);

        Assert.True(broken.RoundTripTicks < 0);
        Assert.False(estimator.TryAdd(broken));
        Assert.False(estimator.TryGetOffset(out _, out _));
    }

    [Fact]
    public void MinimumRoundTripSample_IsTheOneChosen()
    {
        var estimator = new AudioClockOffsetEstimator();
        // 三个样本共享同一个真值 offset = 40，但排队延迟各不相同。
        // 只有 RTT 最小那个的不对称度最低，故它的 offset 最接近真值。
        estimator.TryAdd(Sample(offsetMs: 40, outboundMs: 30, inboundMs: 2));
        estimator.TryAdd(Sample(offsetMs: 40, outboundMs: 1, inboundMs: 1));
        estimator.TryAdd(Sample(offsetMs: 40, outboundMs: 12, inboundMs: 9));

        Assert.True(estimator.TryGetOffset(out var offset, out var rtt));
        Assert.Equal(2 * Ms, rtt);
        Assert.Equal(40 * Ms, offset);
    }

    [Fact]
    public void SingleOutlier_DoesNotMoveTheEstimate()
    {
        // 与取平均的对照。这一组里若取平均，offset 会被那个 200 毫秒的离群样本
        // 拉走几十毫秒；取最小 RTT 则完全不受它影响。
        var estimator = new AudioClockOffsetEstimator();
        estimator.TryAdd(Sample(offsetMs: 40, outboundMs: 1, inboundMs: 1));
        estimator.TryAdd(Sample(offsetMs: 40, outboundMs: 200, inboundMs: 2));

        Assert.True(estimator.TryGetOffset(out var offset, out _));
        Assert.Equal(40 * Ms, offset);
    }

    [Fact]
    public void FullWindow_OverwritesTheOldestSample()
    {
        var estimator = new AudioClockOffsetEstimator();
        // 第一个样本是 RTT 最小的那个。灌满一圈之后它应当已被挤出，
        // 于是估计值改由后来者决定——窗口是「最近 8 次」而不是「历史最优」。
        estimator.TryAdd(Sample(offsetMs: 40, outboundMs: 1, inboundMs: 1));
        for (var i = 0; i < AudioClockOffsetEstimator.WindowSize; i++)
        {
            estimator.TryAdd(Sample(offsetMs: 40, outboundMs: 5, inboundMs: 5));
        }

        Assert.True(estimator.TryGetOffset(out _, out var rtt));
        Assert.Equal(10 * Ms, rtt);
    }

    [Fact]
    public void RoundTripBeyondTheCap_ReportsUnavailable()
    {
        // 60 毫秒 RTT 意味着 offset 误差可达 30 毫秒，已吃掉整个 10 毫秒预算。
        // 此时报不可用，让上层退回缓冲深度控制，而不是拿一个错值去对齐。
        var estimator = new AudioClockOffsetEstimator();
        estimator.TryAdd(Sample(offsetMs: 40, outboundMs: 40, inboundMs: 20));

        Assert.False(estimator.TryGetOffset(out _, out _));
    }

    [Fact]
    public void RoundTripExactlyAtTheCap_IsStillAccepted()
    {
        // 边界取「等于上限仍可用」。夹在两条判据之间是刻意的：只测超出会让
        // 比较符从 > 改成 >= 时无人发现。
        var estimator = new AudioClockOffsetEstimator();
        estimator.TryAdd(Sample(offsetMs: 40, outboundMs: 25, inboundMs: 25));

        Assert.True(estimator.TryGetOffset(out _, out var rtt));
        Assert.Equal(AudioClockOffsetEstimator.MaxAcceptableRoundTrip.Ticks, rtt);
    }

    [Fact]
    public void Reset_DiscardsSamplesFromThePreviousConnection()
    {
        var estimator = new AudioClockOffsetEstimator();
        estimator.TryAdd(Sample(offsetMs: 40, outboundMs: 1, inboundMs: 1));
        Assert.True(estimator.TryGetOffset(out _, out _));

        estimator.Reset();

        Assert.False(estimator.TryGetOffset(out _, out _));
    }
}
