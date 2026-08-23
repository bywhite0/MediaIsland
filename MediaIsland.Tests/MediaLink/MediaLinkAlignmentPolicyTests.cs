using MediaIsland.Services.MediaLink;
using MediaIsland.Services.MediaLink.Protocol;
using Xunit;

namespace MediaIsland.Tests.MediaLink;

public class MediaLinkAlignmentPolicyTests
{
    /// <summary>native 侧 MIN_TARGET_MS 的对端值。</summary>
    private const int MinTargetMs = 50;

    private static MediaLinkAlignmentDecision Decide(
        bool capability = true,
        long? budgetMs = 300,
        double deviceLatencyMs = 10,
        bool offsetAvailable = true) =>
        MediaLinkAlignmentPolicy.Decide(
            capability, budgetMs, deviceLatencyMs, MinTargetMs, offsetAvailable);

    [Fact]
    public void EverythingInPlace_Aligns()
    {
        var decision = Decide();

        Assert.True(decision.IsAligned);
        Assert.Equal(MediaLinkAlignmentState.Aligned, decision.State);
    }

    [Fact]
    public void MissingCapability_IsNotAligned()
    {
        var decision = Decide(capability: false);

        Assert.False(decision.IsAligned);
        Assert.Equal(MediaLinkAlignmentState.NoCapability, decision.State);
    }

    [Fact]
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

    [Fact]
    public void ABudgetThatCannotHoldTheMinimumBuffer_FallsBack()
    {
        // 预算 55、设备延迟 10，余量 45 装不下最小缓冲 50。
        var decision = Decide(budgetMs: 55, deviceLatencyMs: 10);

        Assert.Equal(MediaLinkAlignmentState.BudgetTooSmall, decision.State);
        // 边界成对：余量恰等于最小缓冲时要对齐。
        Assert.True(Decide(budgetMs: 60, deviceLatencyMs: 10).IsAligned);
    }

    [Fact]
    public void ASlowDeviceEatsTheHeadroom()
    {
        // 同一个预算，设备延迟大到吃掉余量就对不齐——蓝牙耳机可达 100 至 200 毫秒。
        Assert.True(Decide(budgetMs: 300, deviceLatencyMs: 100).IsAligned);
        Assert.Equal(
            MediaLinkAlignmentState.BudgetTooSmall,
            Decide(budgetMs: 300, deviceLatencyMs: 260).State);
    }

    [Fact]
    public void ANonPositiveBudgetIsInfeasibleRatherThanUndeclared()
    {
        // 0 与负数是「声明了一个办不到的值」，不是「没声明」。两者的排查方向不同：
        // 前者去改服务端的配置值，后者去查服务端版本。
        Assert.Equal(MediaLinkAlignmentState.BudgetTooSmall, Decide(budgetMs: 0).State);
        Assert.Equal(MediaLinkAlignmentState.BudgetTooSmall, Decide(budgetMs: -1).State);
    }

    [Fact]
    public void AMissingClockOffset_IsReportedAsSuchAndOnlyLast()
    {
        var decision = Decide(offsetAvailable: false);

        Assert.Equal(MediaLinkAlignmentState.NoClockOffset, decision.State);
    }

    [Fact]
    public void APermanentObstacleOutranksTheTransientOne()
    {
        // 时钟没对上是唯一会自己好转的一条，故它排在最后。排在前面的话，一台预算
        // 根本不够的机器在刚连上那几秒会报「还没对上时钟」——那条提示指向等待，
        // 于是用户就一直等下去。
        var decision = Decide(budgetMs: 55, deviceLatencyMs: 10, offsetAvailable: false);
        Assert.Equal(MediaLinkAlignmentState.BudgetTooSmall, decision.State);

        var noCapability = Decide(capability: false, budgetMs: null, offsetAvailable: false);
        Assert.Equal(MediaLinkAlignmentState.NoCapability, noCapability.State);

        var noBudget = Decide(budgetMs: null, offsetAvailable: false);
        Assert.Equal(MediaLinkAlignmentState.NoBudgetDeclared, noBudget.State);
    }

    [Fact]
    public void EveryReasonIsDistinct()
    {
        // 四条出路的差别只在归因上——四条都让声音照常出。合成一条「对齐不可用」，
        // 用户就无从知道该查服务端版本、服务端配置、自己的设备、还是网络。
        var reasons = new[]
        {
            Decide().Reason,
            Decide(capability: false).Reason,
            Decide(budgetMs: null).Reason,
            Decide(budgetMs: 55).Reason,
            Decide(offsetAvailable: false).Reason
        };

        Assert.Equal(reasons.Length, reasons.Distinct().Count());
        Assert.All(reasons, reason => Assert.False(string.IsNullOrWhiteSpace(reason)));

        // 预算不足那条要带上三个数，否则用户不知道该调哪一个。
        var tooSmall = Decide(budgetMs: 55, deviceLatencyMs: 10).Reason;
        Assert.Contains("55", tooSmall);
        Assert.Contains("10", tooSmall);
        Assert.Contains("50", tooSmall);
    }

    [Fact]
    public void ADeclaredBudgetIsRereadOnEveryDecision()
    {
        // server.hello 可以在连接存活期间重发并改 D，故判定不得缓存上一次的结论：
        // 同一台机器要能在两个方向上切换。
        Assert.True(Decide(budgetMs: 300).IsAligned);
        Assert.False(Decide(budgetMs: 55).IsAligned);
        Assert.True(Decide(budgetMs: 300).IsAligned);
    }
}
