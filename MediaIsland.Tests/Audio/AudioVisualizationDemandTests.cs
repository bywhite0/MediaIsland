using MediaIsland.Services.Audio.Visualization;
using Xunit;

namespace MediaIsland.Tests.Audio;

/// <summary>
/// 需求对外是布尔状态，内部才是计数。这层区分是刻意的：下游收到通知后重算
/// 「现在该不该采集」，重算幂等，漏一次通知最多延迟到下一次事件即自愈；
/// 若下游拿到的是增减指令，漏一次就永久错位，且错位无法自愈。
///
/// 故这里的断言重点不在计数准不准，而在「事件只在状态真正翻转时触发」——
/// 第二次注册再触发一次不会导致错误结果，但会让下游为一个没变的状态白重算，
/// 而重算的代价是一次 WebSocket 往返加一次音频设备启停。
/// </summary>
public class AudioVisualizationDemandTests
{
    [Fact]
    public void Initially_IsNotDemanded()
    {
        var demand = new AudioVisualizationDemand();

        Assert.False(demand.IsDemanded);
        Assert.Equal(0, demand.Count);
    }

    [Fact]
    public void FirstRegistration_FlipsStateAndRaisesOnce()
    {
        var demand = new AudioVisualizationDemand();
        var raised = 0;
        demand.DemandChanged += () => raised++;

        demand.Register();

        Assert.True(demand.IsDemanded);
        Assert.Equal(1, demand.Count);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void SecondRegistration_DoesNotRaise()
    {
        var demand = new AudioVisualizationDemand();
        demand.Register();

        var raised = 0;
        demand.DemandChanged += () => raised++;
        demand.Register();

        // 状态本来就是「有需求」，没有翻转。触发只会让下游白跑一次重算。
        Assert.Equal(2, demand.Count);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void ReleasingOneOfTwo_KeepsDemandAndStaysQuiet()
    {
        var demand = new AudioVisualizationDemand();
        var first = demand.Register();
        demand.Register();

        var raised = 0;
        demand.DemandChanged += () => raised++;
        first.Dispose();

        // 还剩一个组件在看，采集不能停。
        Assert.True(demand.IsDemanded);
        Assert.Equal(1, demand.Count);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void ReleasingLast_FlipsStateAndRaisesOnce()
    {
        var demand = new AudioVisualizationDemand();
        var only = demand.Register();

        var raised = 0;
        demand.DemandChanged += () => raised++;
        only.Dispose();

        Assert.False(demand.IsDemanded);
        Assert.Equal(0, demand.Count);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void DisposingTheSameRegistrationTwice_IsIdempotent()
    {
        // 组件的 Unloaded 未必只走一次（重新挂载、框架的清理路径都可能再调）。
        // 计数掉成负数的后果是隐蔽而致命的：后续 Register 把 -1 加成 0，
        // 状态翻不回真，采集从此再也起不来，且没有任何报错。
        var demand = new AudioVisualizationDemand();
        var registration = demand.Register();

        var raised = 0;
        demand.DemandChanged += () => raised++;
        registration.Dispose();
        registration.Dispose();

        Assert.Equal(0, demand.Count);
        Assert.False(demand.IsDemanded);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void SubsequentRegistrationAfterFullRelease_FlipsStateAgain()
    {
        // 上一条描述的失效模式，从正面锁一遍：全部释放后再注册，必须能重新起来。
        var demand = new AudioVisualizationDemand();
        var first = demand.Register();
        first.Dispose();
        first.Dispose();

        var raised = 0;
        demand.DemandChanged += () => raised++;
        demand.Register();

        Assert.True(demand.IsDemanded);
        Assert.Equal(1, demand.Count);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void ConcurrentRegisterAndRelease_SettlesAtZero()
    {
        // 组件在 UI 线程挂载/卸载，而需求状态会被上游服务在别的线程上读。
        // 用非原子的 ++/-- 时这条会随机剩下几个计数，且失败频率低到能混过 CI。
        var demand = new AudioVisualizationDemand();

        Parallel.For(0, 200, _ =>
        {
            using var registration = demand.Register();
        });

        Assert.Equal(0, demand.Count);
        Assert.False(demand.IsDemanded);
    }
}
