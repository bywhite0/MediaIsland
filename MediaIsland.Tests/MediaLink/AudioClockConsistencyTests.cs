using MediaIsland.Services.MediaLink;
using Xunit;

namespace MediaIsland.Tests.MediaLink;

/// <summary>
/// 对时窗口一致性检查的判据。检查的立论：每个被接受样本的估计误差有硬界
/// |offset_i − 真值| ≤ rtt_i / 2，故观测同一真值的任意两样本必满足
/// |offset_i − offset_j| ≤ (rtt_i + rtt_j) / 2 + 漂移余量；违反即整窗不可信。
/// 它要抓的是估计器原本看不见的共模偏移：错单位的对端、换机重连的残样。
/// </summary>
public class AudioClockConsistencyTests
{
    private const long Ms = TimeSpan.TicksPerMillisecond;

    /// <summary>真值 offset，40 毫秒。所有好样本观测同一个它。</summary>
    private const long TrueOffsetTicks = 40 * Ms;

    /// <summary>
    /// 与 AudioClockOffsetTests 同形的构造：真值 offset + 去程 + 回程 + 处理耗时。
    /// 判据说的是「已知真值能否被还原、已知坏窗能否被拒」，不直接填四个数。
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

    /// <summary>
    /// 同一构造的 tick 精度版。margin 边界判据要控制到一个 tick，
    /// double 毫秒经不起截断，其余判据仍用毫秒版。
    /// </summary>
    private static AudioClockSample SampleTicks(long offsetTicks, long outboundTicks, long inboundTicks)
    {
        var t1 = 0L;
        var t2 = outboundTicks + offsetTicks;
        var t3 = t2;
        var t4 = outboundTicks + inboundTicks;
        return new AudioClockSample(t1, t2, t3, t4);
    }

    /// <summary>
    /// 错单位对端在 nowTicks 时刻被探测到的样本：服务端时钟读数是 tick 语义，
    /// 上报时却按毫秒计。去回各 1 毫秒、处理零耗时，RTT 看起来完全正常；
    /// offset 却以每秒约一秒的速率漂移——相邻两次探测（快速阶段 200 毫秒）
    /// 就差约 200 毫秒，而一致性上界只有 (2ms + 2ms) / 2 + 1ms = 3 毫秒。
    /// </summary>
    private static AudioClockSample WrongUnitSample(long nowTicks)
    {
        var t1 = nowTicks;
        var serverTicksAtReceipt = nowTicks + Ms + TrueOffsetTicks;
        var t2 = serverTicksAtReceipt / Ms;
        var t3 = t2;
        var t4 = nowTicks + 2 * Ms;
        return new AudioClockSample(t1, t2, t3, t4);
    }

    [Fact]
    public void SymmetricGoodWindow_StillYieldsOffset()
    {
        // 回归：观测同一真值的对称样本互相印证（offset 差为零），检查不得误杀。
        var estimator = new AudioClockOffsetEstimator();
        estimator.TryAdd(Sample(offsetMs: 40, outboundMs: 3, inboundMs: 3));
        estimator.TryAdd(Sample(offsetMs: 40, outboundMs: 1, inboundMs: 1));
        estimator.TryAdd(Sample(offsetMs: 40, outboundMs: 8, inboundMs: 8));

        Assert.True(estimator.TryGetOffset(out var offset, out _));
        Assert.Equal(TrueOffsetTicks, offset);
    }

    [Fact]
    public void AsymmetricGoodWindow_StaysAvailable()
    {
        // 回归的另一半：不对称让两个好样本的 offset 相差 7 毫秒（各偏 3、4 毫秒，
        // 都在 rtt/2 硬界内），必须靠上界里的两个 rtt/2 相加才放行——
        // 上界少算任何一边，RTT 不对称的好窗就会被误杀。
        var estimator = new AudioClockOffsetEstimator();
        estimator.TryAdd(Sample(offsetMs: 40, outboundMs: 7, inboundMs: 1));
        estimator.TryAdd(Sample(offsetMs: 40, outboundMs: 1, inboundMs: 9));

        Assert.True(estimator.TryGetOffset(out var offset, out var rtt));
        Assert.Equal(8 * Ms, rtt);
        Assert.Equal(43 * Ms, offset);
    }

    [Fact]
    public void WrongUnitPeer_SecondSampleMakesWindowUnavailable()
    {
        // 单个错单位样本无对可查（见 SingleSampleWindow_PassesVacuously），
        // 第二个进窗即互相对质失败——不用等窗口填满。
        var estimator = new AudioClockOffsetEstimator();
        Assert.True(estimator.TryAdd(WrongUnitSample(0)));
        Assert.True(estimator.TryGetOffset(out _, out _));

        Assert.True(estimator.TryAdd(WrongUnitSample(200 * Ms)));

        Assert.False(estimator.TryGetOffset(out _, out _));
    }

    [Fact]
    public void MixedWindow_UnavailableUntilBadSampleRollsOut()
    {
        // 换机残样：断连清空义务在对端失效时，旧机器的样本混进新窗口，
        // offset 跳变以开机时长计——这里取 5 秒。混窗期间无从分辨谁坏，整窗不可信；
        // 坏样本随窗口滚动流出后，剩下的好样本重新互相印证，恢复可用。
        var estimator = new AudioClockOffsetEstimator();
        estimator.TryAdd(Sample(offsetMs: 40, outboundMs: 1, inboundMs: 1));
        estimator.TryAdd(Sample(offsetMs: 40, outboundMs: 3, inboundMs: 3));
        estimator.TryAdd(Sample(offsetMs: 5040, outboundMs: 3, inboundMs: 3));

        Assert.False(estimator.TryGetOffset(out _, out _));

        for (var i = 0; i < AudioClockOffsetEstimator.WindowSize; i++)
        {
            estimator.TryAdd(Sample(offsetMs: 40, outboundMs: 2, inboundMs: 2));
        }

        Assert.True(estimator.TryGetOffset(out var offset, out _));
        Assert.Equal(TrueOffsetTicks, offset);
    }

    [Fact]
    public void PersistentlyBadPeer_StaysUnavailable_WindowIsNotCleared()
    {
        // 钉「违反不清窗」。若违反时清窗，下一个样本就落进单样本窗真空通过，
        // 坏 offset 周期性泄漏。这里在首次违反后继续喂满三个窗口的样本，
        // 每一步都必须仍不可用。
        var estimator = new AudioClockOffsetEstimator();
        estimator.TryAdd(WrongUnitSample(0));

        for (var k = 1; k < 3 * AudioClockOffsetEstimator.WindowSize; k++)
        {
            estimator.TryAdd(WrongUnitSample(k * 200 * Ms));

            Assert.False(estimator.TryGetOffset(out _, out _));
        }
    }

    [Fact]
    public void OffsetGapExactlyAtTheBound_IsStillConsistent()
    {
        // 边界成对的通过侧。两个满不对称的样本把 rtt/2 硬界吃满（各偏 3 毫秒、
        // 方向相反），真值本身再漂移恰好一个 margin（1 毫秒）：
        // |43ms − 36ms| = 7ms == (6ms + 6ms) / 2 + 1ms。恰在界上必须通过——
        // 只测超界会让比较符从「大于」改成「大于等于」时无人发现。
        var estimator = new AudioClockOffsetEstimator();
        estimator.TryAdd(SampleTicks(TrueOffsetTicks, outboundTicks: 6 * Ms, inboundTicks: 0));
        estimator.TryAdd(SampleTicks(
            TrueOffsetTicks - AudioClockOffsetEstimator.ConsistencyMarginTicks,
            outboundTicks: 0, inboundTicks: 6 * Ms));

        Assert.True(estimator.TryGetOffset(out _, out _));
    }

    [Fact]
    public void OffsetGapOneTickBeyondTheBound_IsInconsistent()
    {
        // 边界成对的拒绝侧：与上一条只差一个 tick 的真值漂移，必须被拒。
        var estimator = new AudioClockOffsetEstimator();
        estimator.TryAdd(SampleTicks(TrueOffsetTicks, outboundTicks: 6 * Ms, inboundTicks: 0));
        estimator.TryAdd(SampleTicks(
            TrueOffsetTicks - AudioClockOffsetEstimator.ConsistencyMarginTicks - 1,
            outboundTicks: 0, inboundTicks: 6 * Ms));

        Assert.False(estimator.TryGetOffset(out _, out _));
    }

    [Fact]
    public void SingleSampleWindow_PassesVacuously()
    {
        // 断言现状：单样本无对可查，检查真空通过，坏 offset 会被原样给出。
        // 接受这个残量的论证——错值最多活到第二个样本到达（快速阶段 200 毫秒），
        // 外环速率上限 0.5 毫秒每秒把这段时间的损害封在 0.1 毫秒量级，不另设防。
        // 若将来有人给单样本窗另设防，这条判据要求他先来改掉这段论证。
        var estimator = new AudioClockOffsetEstimator();
        var wrongUnit = WrongUnitSample(0);
        estimator.TryAdd(wrongUnit);

        Assert.True(estimator.TryGetOffset(out var offset, out _));
        Assert.Equal(wrongUnit.OffsetTicks, offset);
    }

    [Fact]
    public void InconsistentWindows_CountsEachUnavailableEvaluation()
    {
        // 计数语义：每次因不一致而报不可用记一次；空窗与 RTT 超上限的不可用不计；
        // 只增，Reset 不清——与探测壳的 Misses 等诊断计数同形，跨连接累计。
        var estimator = new AudioClockOffsetEstimator();
        Assert.Equal(0, estimator.InconsistentWindows);

        Assert.False(estimator.TryGetOffset(out _, out _));
        Assert.Equal(0, estimator.InconsistentWindows);

        estimator.TryAdd(Sample(offsetMs: 40, outboundMs: 1, inboundMs: 1));
        estimator.TryAdd(Sample(offsetMs: 40, outboundMs: 3, inboundMs: 3));
        Assert.True(estimator.TryGetOffset(out _, out _));
        Assert.Equal(0, estimator.InconsistentWindows);

        estimator.TryAdd(Sample(offsetMs: 5040, outboundMs: 3, inboundMs: 3));
        Assert.False(estimator.TryGetOffset(out _, out _));
        Assert.Equal(1, estimator.InconsistentWindows);
        Assert.False(estimator.TryGetOffset(out _, out _));
        Assert.Equal(2, estimator.InconsistentWindows);

        estimator.Reset();
        estimator.TryAdd(Sample(offsetMs: 40, outboundMs: 1, inboundMs: 1));
        Assert.True(estimator.TryGetOffset(out _, out _));
        Assert.Equal(2, estimator.InconsistentWindows);
    }

    [Fact]
    public void RoundTripCapUnavailability_DoesNotCountAsInconsistent()
    {
        // 与上一条互补：不可用的三种原因里只有「不一致」计入这个数。
        // 60 毫秒 RTT 的单样本窗真空一致，但超上限——不可用而计数不动。
        var estimator = new AudioClockOffsetEstimator();
        estimator.TryAdd(Sample(offsetMs: 40, outboundMs: 40, inboundMs: 20));

        Assert.False(estimator.TryGetOffset(out _, out _));
        Assert.Equal(0, estimator.InconsistentWindows);
    }
}
