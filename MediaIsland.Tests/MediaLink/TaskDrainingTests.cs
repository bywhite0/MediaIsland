using MediaIsland.Services.MediaLink;
using Xunit;

namespace MediaIsland.Tests.MediaLink;

/// <summary>
/// 有界等待。
///
/// 这组判据的形态本身是要害。若断言写成直接 await DrainAsync(...)，那么把 limit 变异成
/// InfiniteTimeSpan 之后测试会挂住而不是变红——那正是本期要消灭的失效形态在判据里重演。
/// 故一律经 <see cref="WithinAsync"/>：它把「没在界内返回」表达为一次断言失败。
/// </summary>
public class TaskDrainingTests
{
    /// <summary>判据用的外层界。远大于各用例自己的 limit，故它只在真的无界时才触发。</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    private static async Task<int> WithinAsync(Task<int> drain, TimeSpan bound)
    {
        var winner = await Task.WhenAny(drain, Task.Delay(bound));
        Assert.True(
            ReferenceEquals(winner, drain),
            $"DrainAsync 没有在 {bound.TotalMilliseconds}ms 内返回");
        return await drain;
    }

    [Fact]
    public async Task EmptyList_ReturnsZeroWithoutWaiting()
    {
        var drain = TaskDraining.DrainAsync([], TimeSpan.FromMinutes(10));

        Assert.Equal(0, await WithinAsync(drain, Bound));
    }

    [Fact]
    public async Task AllCompleted_ReturnsZero()
    {
        var drain = TaskDraining.DrainAsync(
            [Task.CompletedTask, Task.CompletedTask], TimeSpan.FromMinutes(10));

        Assert.Equal(0, await WithinAsync(drain, Bound));
    }

    [Fact]
    public async Task NeverCompleting_ReturnsUnfinishedCountAfterLimit()
    {
        var never = new TaskCompletionSource();
        var alsoNever = new TaskCompletionSource();

        // 地基：先钉住确实有没完成的。只断言返回 2 的话，「压根没等就返回」也会通过。
        Assert.False(never.Task.IsCompleted);
        Assert.False(alsoNever.Task.IsCompleted);

        var drain = TaskDraining.DrainAsync(
            [never.Task, alsoNever.Task, Task.CompletedTask], TimeSpan.FromMilliseconds(150));

        Assert.Equal(2, await WithinAsync(drain, Bound));
    }

    [Fact]
    public async Task ZeroLimit_DoesNotWait()
    {
        var never = new TaskCompletionSource();

        var drain = TaskDraining.DrainAsync([never.Task], TimeSpan.Zero);

        Assert.Equal(1, await WithinAsync(drain, Bound));
    }

    [Fact]
    public async Task PreCancelledToken_ReturnsImmediately()
    {
        var never = new TaskCompletionSource();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var drain = TaskDraining.DrainAsync([never.Task], TimeSpan.FromMinutes(10), cts.Token);

        Assert.Equal(1, await WithinAsync(drain, Bound));
    }

    [Fact]
    public async Task InfiniteTimeSpan_IsTreatedAsNoWait()
    {
        // Timeout.InfiniteTimeSpan 是 -1ms，故它落在「零或负即不等待」那一条上。
        // 钉住这一点是因为它反直觉：名字读起来像「等到底」，而在一个专门为有界性存在的
        // 函数里那恰恰是唯一不能有的行为。少了这条判据，将来有人把负值分支改成
        // 「无界等待」不会有任何东西发现，而那会把挂起重新引进停服路径。
        var never = new TaskCompletionSource();

        var drain = TaskDraining.DrainAsync([never.Task], Timeout.InfiniteTimeSpan);

        Assert.Equal(1, await WithinAsync(drain, Bound));
    }

    /// <summary>
    /// 停服排水期限必须从 <see cref="MediaLinkSession.SendTimeout"/> 推导，不得是字面量。
    ///
    /// 这条判据的区分力有个前提要说清：把推导式换成今天等值的字面量（6s），本判据仍会通过。
    /// 它真正拦住的是那之后的一步——SendTimeout 被调大而字面量没跟着大。那正是推导式
    /// 存在的理由，也正是「排完队后仍在飞的最长操作是一次发送」这条论证会失效的方式。
    /// </summary>
    [Fact]
    public void ShutdownDrainTimeout_TracksSendTimeout()
    {
        Assert.Equal(
            MediaLinkSession.SendTimeout + TimeSpan.FromSeconds(1),
            MediaLinkServer.ShutdownDrainTimeout);

        // 地基：界必须真的比单次发送的界大。相等或更小的话，一次正常的慢发送
        // 就会被算成「没排干」，warning 会变成常态噪声。
        Assert.True(
            MediaLinkServer.ShutdownDrainTimeout > MediaLinkSession.SendTimeout,
            "排水期限必须严格大于单帧发送超时");
    }
}
