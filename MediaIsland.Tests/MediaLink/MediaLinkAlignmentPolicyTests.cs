using MediaIsland.Services.Audio.Playback.Native;
using MediaIsland.Services.MediaLink;
using MediaIsland.Services.MediaLink.Protocol;
using MediaIsland.Tests.Infrastructure;
using Xunit;

namespace MediaIsland.Tests.MediaLink;

public class MediaLinkAlignmentPolicyTests
{
    /// <summary>
    /// native 侧 MIN_TARGET_MS，经 FFI 导出取得。不在托管侧抄一份 50：
    /// 同一个数分散成两份各自为真的声明时，改一处而漏另一处不会让任何判据变红。
    ///
    /// 属性而非 static readonly 字段：字段在类型初始化器里 P/Invoke，缺库环境
    /// 整类死于 TypeInitializationException，NativeAudioFact 的跳过根本轮不到生效；
    /// 属性把取值推迟进判据体，那时门控已经放行或跳过。
    /// </summary>
    private static int MinTargetMs => (int)WasapiRenderer.TargetMsBounds().MinMs;

    private static MediaLinkAlignmentDecision Decide(
        bool capability = true,
        long? budgetMs = 300,
        double deviceLatencyMs = 10,
        double deviceBufferMs = 0,
        bool offsetAvailable = true) =>
        MediaLinkAlignmentPolicy.Decide(
            capability, budgetMs, deviceLatencyMs, deviceBufferMs, MinTargetMs, offsetAvailable);

    [NativeAudioFact]
    public void EverythingInPlace_Aligns()
    {
        var decision = Decide();

        Assert.True(decision.IsAligned);
        Assert.Equal(MediaLinkAlignmentState.Aligned, decision.State);
    }

    [NativeAudioFact]
    public void MissingCapability_IsNotAligned()
    {
        var decision = Decide(capability: false);

        Assert.False(decision.IsAligned);
        Assert.Equal(MediaLinkAlignmentState.NoCapability, decision.State);
    }

    [NativeAudioFact]
    public void MissingBudget_IsTreatedAsUnsupported_NotAsTheDefault()
    {
        // 缺失时回落到默认 300 会让两端各自默认成同一个数：看起来一致，实则两份
        // 各自为真的声明，改了一端就静默失配，而症状是两台机器差一个固定的量。
        var decision = Decide(budgetMs: null);

        Assert.False(decision.IsAligned);
        Assert.Equal(MediaLinkAlignmentState.NoBudgetDeclared, decision.State);

        // 同一组入参，只是把声明补上，就该对齐——证明上面那条红是缺声明造成的，
        // 而不是别的哪一项不满足。
        Assert.True(Decide(budgetMs: MediaLinkProtocol.AudioClockDefaultBudgetMs).IsAligned);
    }

    [NativeAudioFact]
    public void ABudgetThatCannotHoldTheMinimumBuffer_FallsBack()
    {
        // 预算 = 下限 + 5、设备延迟 10：余量比最小缓冲差 5 毫秒，装不下。
        var decision = Decide(budgetMs: MinTargetMs + 5, deviceLatencyMs: 10);

        Assert.Equal(MediaLinkAlignmentState.BudgetTooSmall, decision.State);
        // 边界成对：余量恰等于最小缓冲时要对齐。
        Assert.True(Decide(budgetMs: MinTargetMs + 10, deviceLatencyMs: 10).IsAligned);
    }

    [NativeAudioFact]
    public void ASlowDeviceEatsTheHeadroom()
    {
        // 同一个预算，设备延迟大到吃掉余量就对不齐——蓝牙耳机可达 100 至 200 毫秒。
        Assert.True(Decide(budgetMs: 300, deviceLatencyMs: 100).IsAligned);
        Assert.Equal(
            MediaLinkAlignmentState.BudgetTooSmall,
            Decide(budgetMs: 300, deviceLatencyMs: 260).State);
    }

    [NativeAudioFact]
    public void TheEndpointBufferCapacityCountsTowardTheFloor()
    {
        // 渲染循环每轮把可写帧全写满，一个采样最坏要等整整一个缓冲容量才被取走。
        // 下限算式缺这一项就是系统性低估约 20 毫秒：预算落在下限附近的机器会判
        // 「能对齐」而实际达不到，表现是缓冲被压到下限后误差永久为正、外环撞边界告警。
        Assert.Equal(
            MediaLinkAlignmentState.BudgetTooSmall,
            Decide(budgetMs: MinTargetMs + 30, deviceLatencyMs: 10, deviceBufferMs: 25).State);

        // 边界成对：容量缩到余量恰好装得下时要对齐。
        Assert.True(Decide(budgetMs: MinTargetMs + 30, deviceLatencyMs: 10, deviceBufferMs: 20).IsAligned);
    }

    [NativeAudioFact]
    public void TheBufferReasonNamesTheCapacitySoTheUserKnowsWhatToChange()
    {
        // 四个数各是一个可动的旋钮：预算（服务端配置）、设备延迟（换设备）、
        // 缓冲容量（设备定的，换设备）、最小缓冲（本机常量）。少报一个就少一条排查路。
        var reason = Decide(budgetMs: MinTargetMs + 30, deviceLatencyMs: 10, deviceBufferMs: 25).Reason;

        Assert.Contains("25.0", reason);
    }

    [NativeAudioFact]
    public void ANonPositiveBudgetIsInfeasibleRatherThanUndeclared()
    {
        // 0 与负数是「声明了一个办不到的值」，不是「没声明」。两者的排查方向不同：
        // 前者去改服务端的配置值，后者去查服务端版本。
        Assert.Equal(MediaLinkAlignmentState.BudgetTooSmall, Decide(budgetMs: 0).State);
        Assert.Equal(MediaLinkAlignmentState.BudgetTooSmall, Decide(budgetMs: -1).State);
    }

    [NativeAudioFact]
    public void AnAbsurdlyLargeBudgetIsInfeasibleRatherThanAligned()
    {
        // 极大的预算与太小的预算同类，都是「声明了一个办不到的值」。10^15 毫秒是三万年，
        // 它在下界那一关绰绰有余，没有上界就一路走成「正在对齐」。
        Assert.Equal(
            MediaLinkAlignmentState.BudgetTooSmall,
            Decide(budgetMs: 1_000_000_000_000_000).State);

        // 边界成对：恰好等于上界要对齐，超出一毫秒就不对齐。
        Assert.True(Decide(budgetMs: MediaLinkAlignmentPolicy.MaxBudgetMs).IsAligned);
        Assert.Equal(
            MediaLinkAlignmentState.BudgetTooSmall,
            Decide(budgetMs: MediaLinkAlignmentPolicy.MaxBudgetMs + 1).State);
    }

    [NativeAudioFact]
    public void TheUpperBoundSitsBelowWhereTheTickConversionOverflows()
    {
        // 算出声时刻要把预算换成 100 纳秒计次，即乘 TicksPerMillisecond。预算超过
        // long.MaxValue / TicksPerMillisecond 时这个乘法溢出，结果是负数——而负预算本该
        // 判「办不到」。上界必须落在溢出点之前，否则判定说「能对齐」，换算却交出一个负数目标。
        const long overflowsAbove = long.MaxValue / TimeSpan.TicksPerMillisecond;
        Assert.True(MediaLinkAlignmentPolicy.MaxBudgetMs < overflowsAbove);

        // 溢出点附近与 long 极值本身都不得走成对齐。
        Assert.Equal(MediaLinkAlignmentState.BudgetTooSmall, Decide(budgetMs: overflowsAbove - 1).State);
        Assert.Equal(MediaLinkAlignmentState.BudgetTooSmall, Decide(budgetMs: overflowsAbove).State);
        Assert.Equal(MediaLinkAlignmentState.BudgetTooSmall, Decide(budgetMs: overflowsAbove + 1).State);
        Assert.Equal(MediaLinkAlignmentState.BudgetTooSmall, Decide(budgetMs: long.MaxValue).State);
    }

    [NativeAudioFact]
    public void AMissingClockOffset_IsReportedAsSuchAndOnlyLast()
    {
        var decision = Decide(offsetAvailable: false);

        Assert.Equal(MediaLinkAlignmentState.NoClockOffset, decision.State);
    }

    [NativeAudioFact]
    public void APermanentObstacleOutranksTheTransientOne()
    {
        // 时钟没对上是唯一会自己好转的一条，故它排在最后。排在前面的话，一台预算
        // 根本不够的机器在刚连上那几秒会报「还没对上时钟」——那条提示指向等待，
        // 于是用户就一直等下去。
        var decision = Decide(budgetMs: MinTargetMs + 5, deviceLatencyMs: 10, offsetAvailable: false);
        Assert.Equal(MediaLinkAlignmentState.BudgetTooSmall, decision.State);

        var noCapability = Decide(capability: false, budgetMs: null, offsetAvailable: false);
        Assert.Equal(MediaLinkAlignmentState.NoCapability, noCapability.State);

        var noBudget = Decide(budgetMs: null, offsetAvailable: false);
        Assert.Equal(MediaLinkAlignmentState.NoBudgetDeclared, noBudget.State);
    }

    [NativeAudioFact]
    public void EveryReasonIsDistinct()
    {
        // 四条出路的差别只在归因上——四条都让声音照常出。合成一条「对齐不可用」，
        // 用户就无从知道该查服务端版本、服务端配置、自己的设备、还是网络。
        // 预算办不到的两个方向共用一个状态，故它们的差别只剩 Reason 那句话：它必须分得开，
        // 否则「把预算调大」与「把预算调小」看起来是同一条提示。
        var reasons = new[]
        {
            Decide().Reason,
            Decide(capability: false).Reason,
            Decide(budgetMs: null).Reason,
            Decide(budgetMs: MinTargetMs + 5).Reason,
            Decide(budgetMs: MediaLinkAlignmentPolicy.MaxBudgetMs + 1).Reason,
            Decide(offsetAvailable: false).Reason
        };

        Assert.Equal(reasons.Length, reasons.Distinct().Count());
        Assert.All(reasons, reason => Assert.False(string.IsNullOrWhiteSpace(reason)));

        // 预算不足那条要带上三个数，否则用户不知道该调哪一个。
        var tooSmall = Decide(budgetMs: MinTargetMs + 5, deviceLatencyMs: 10).Reason;
        Assert.Contains((MinTargetMs + 5).ToString(), tooSmall);
        Assert.Contains("10", tooSmall);
        Assert.Contains(MinTargetMs.ToString(), tooSmall);
    }

    [NativeAudioFact]
    public void ADeclaredBudgetIsRereadOnEveryDecision()
    {
        // server.hello 可以在连接存活期间重发并改 D，故判定不得缓存上一次的结论：
        // 同一台机器要能在两个方向上切换。
        Assert.True(Decide(budgetMs: 300).IsAligned);
        Assert.False(Decide(budgetMs: MinTargetMs + 5).IsAligned);
        Assert.True(Decide(budgetMs: 300).IsAligned);
    }
}
