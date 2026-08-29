using MediaIsland.Services.Audio.Playback;
using Xunit;

namespace MediaIsland.Tests.Audio;

/// <summary>
/// OutputLatencyTracker 的判据。target 统一取 150ms,与默认目标缓冲深度同量级。
/// 断言读 Current.TotalMilliseconds:存储与断言都在毫秒域,数值直读无换算。
/// </summary>
public class OutputLatencyTrackerTests
{
    private const int TargetMs = 150;

    private static OutputLatencyTracker NewTracker() => new(TargetMs);

    private static double CurrentMs(OutputLatencyTracker tracker) => tracker.Current.TotalMilliseconds;

    [Fact]
    public void FifoArm_FollowsPaddingOffset()
    {
        // 核心场景:接收端卡顿垫零后缓冲占用抬升,FIFO 臂读占用即读偏移。
        var tracker = NewTracker();

        tracker.Sample(hasStarted: true, clockOffsetAvailable: false, ringMs: 150, playTimeErrorUs: 0);
        Assert.Equal(150, CurrentMs(tracker));

        tracker.Sample(hasStarted: true, clockOffsetAvailable: false, ringMs: 450, playTimeErrorUs: 0);
        Assert.Equal(450, CurrentMs(tracker));
    }

    [Fact]
    public void Deadband_HoldsWithinNoise()
    {
        // 量化噪声形态的波动(每步与当前输出差不超过 20,小于死区 25)全程维持,
        // 稳态下输出恒定,呈现不抖。
        var tracker = NewTracker();

        foreach (var ringMs in new double[] { 150, 130, 170, 145, 165 })
        {
            tracker.Sample(hasStarted: true, clockOffsetAvailable: false, ringMs: ringMs, playTimeErrorUs: 0);
            Assert.Equal(150, CurrentMs(tracker));
        }
    }

    [Fact]
    public void Deadband_LiteralPin()
    {
        // 判据引用常量自身会随误改漂移,字面钉防静默。
        Assert.Equal(25, OutputLatencyTracker.DeadbandMs);
    }

    [Fact]
    public void AlignedArm_FollowsTransientError()
    {
        // 对齐臂读 target 加对齐误差;ringMs 传 150 作干扰项,选路正确则被无视。
        var tracker = NewTracker();

        tracker.Sample(hasStarted: true, clockOffsetAvailable: true, ringMs: 150, playTimeErrorUs: 300_000);
        Assert.Equal(450, CurrentMs(tracker));
    }

    [Fact]
    public void AlignedArm_StepsBackAsErrorConverges()
    {
        // 对齐控制律把误差收敛回零的过程中,输出逐步回落。每步差 150 超死区,逐步断言。
        var tracker = NewTracker();
        tracker.Sample(hasStarted: true, clockOffsetAvailable: true, ringMs: 150, playTimeErrorUs: 300_000);
        Assert.Equal(450, CurrentMs(tracker));

        tracker.Sample(hasStarted: true, clockOffsetAvailable: true, ringMs: 150, playTimeErrorUs: 150_000);
        Assert.Equal(300, CurrentMs(tracker));

        tracker.Sample(hasStarted: true, clockOffsetAvailable: true, ringMs: 150, playTimeErrorUs: 0);
        Assert.Equal(150, CurrentMs(tracker));
    }

    [Fact]
    public void FifoArm_FallsBackAfterHardReset()
    {
        // 硬重置清空缓冲后占用回落,输出跟随回落。中途断言必须有:
        // 没有它,恒不更新的实现在终态断言上同样是 150,红不出来。
        var tracker = NewTracker();

        tracker.Sample(hasStarted: true, clockOffsetAvailable: false, ringMs: 450, playTimeErrorUs: 0);
        Assert.Equal(450, CurrentMs(tracker));

        tracker.Sample(hasStarted: true, clockOffsetAvailable: false, ringMs: 150, playTimeErrorUs: 0);
        Assert.Equal(150, CurrentMs(tracker));
    }

    [Fact]
    public void InitialValue_IsTarget()
    {
        // 未采到任何样本前,起播常量仍是最好的估计。
        var tracker = NewTracker();

        Assert.Equal(150, CurrentMs(tracker));
    }

    [Fact]
    public void PrefillLowOccupancy_FollowedExplicitly()
    {
        // prefill 或硬重置重攒期占用从 0 爬回 target,输出先跟到低值再爬回。
        // 该时段本机垫零无声、无听觉参照,此行为是显式接受的设计记账,不是缺陷。
        var tracker = NewTracker();

        tracker.Sample(hasStarted: true, clockOffsetAvailable: false, ringMs: 0, playTimeErrorUs: 0);
        Assert.Equal(0, CurrentMs(tracker));

        tracker.Sample(hasStarted: true, clockOffsetAvailable: false, ringMs: 150, playTimeErrorUs: 0);
        Assert.Equal(150, CurrentMs(tracker));
    }

    [Fact]
    public void NotStarted_HoldsCurrent()
    {
        // 未起播时 stats 全零或瞬时读失败,全零的 ringMs 不得把输出拽回零。
        var tracker = NewTracker();
        tracker.Sample(hasStarted: true, clockOffsetAvailable: false, ringMs: 450, playTimeErrorUs: 0);

        tracker.Sample(hasStarted: false, clockOffsetAvailable: false, ringMs: 0, playTimeErrorUs: 0);
        Assert.Equal(450, CurrentMs(tracker));
    }

    [Fact]
    public void Reset_ReturnsToInitial()
    {
        // 深度变更走停播重启,Reset 回到与构造同语义的初值态。
        var tracker = NewTracker();
        tracker.Sample(hasStarted: true, clockOffsetAvailable: false, ringMs: 450, playTimeErrorUs: 0);

        tracker.Reset(TargetMs);
        Assert.Equal(150, CurrentMs(tracker));
    }

    [Fact]
    public void NegativeRaw_ClampsToZero()
    {
        // 对齐误差极端暂态可把 raw 推到负值(150 + (-300) = -150),
        // 负延迟无物理意义,钳非负在存储侧。ringMs 传 450 作干扰项。
        var tracker = NewTracker();

        tracker.Sample(hasStarted: true, clockOffsetAvailable: true, ringMs: 450, playTimeErrorUs: -300_000);
        Assert.Equal(0, CurrentMs(tracker));
    }

    [Fact]
    public void FifoArm_IgnoresPlayTimeError()
    {
        // FIFO 臂下 error 是干扰项,选路正确则被无视。
        var tracker = NewTracker();

        tracker.Sample(hasStarted: true, clockOffsetAvailable: false, ringMs: 450, playTimeErrorUs: -300_000);
        Assert.Equal(450, CurrentMs(tracker));
    }
}
