namespace MediaIsland.Services.MediaLink;

/// <summary>
/// 有界等待。
///
/// 抽成独立函数不是为了复用——本仓只有停服一条调用路径——而是为了让「有界」这条性质
/// 可被断言。它若只写在 StopAsync 里，测它就需要一个真的卡死的会话：一个不回关闭握手的
/// 对端、一个写满不排空的 socket。抽出来之后一个永不完成的 TaskCompletionSource 就够。
/// </summary>
internal static class TaskDraining
{
    /// <summary>
    /// 等 tasks 全部完成，最多等 limit。返回未在期限内完成的任务数。
    ///
    /// 不抛超时异常是刻意的：唯一的调用方是停服路径，它必须继续走完剩余清理步骤。
    /// 把「没排干」表达为返回值而非异常，调用方就不需要 try 才能往下走。
    ///
    /// 用 WhenAny 而非直接 await WhenAll：后者会把某个任务的异常抛给停服路径，
    /// 而传进来的都是已在自己内部吞掉异常的后台任务，本函数的职责只有「等多久」。
    /// </summary>
    internal static async Task<int> DrainAsync(
        IReadOnlyList<Task> tasks,
        TimeSpan limit,
        CancellationToken cancellationToken = default)
    {
        if (tasks is null || tasks.Count == 0)
        {
            return 0;
        }

        // 零或负的 limit 表示不等待。Timeout.InfiniteTimeSpan 的值是 -1ms，故它也落在这里，
        // 语义是「不等」而不是「等到底」——这在一个专门为有界性而存在的函数里是唯一说得通的
        // 方向：调用方要的若真是无界，它本来就不该调这个函数。
        if (limit <= TimeSpan.Zero || cancellationToken.IsCancellationRequested)
        {
            return CountUnfinished(tasks);
        }

        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var all = Task.WhenAll(tasks);
        var completed = await Task.WhenAny(all, Task.Delay(limit, delayCts.Token)).ConfigureAwait(false);

        if (ReferenceEquals(completed, all))
        {
            // 及时释放定时器：停服可能连排几批，留着会攒下一批没人等的计时器。
            delayCts.Cancel();
            // 标记已观察。WhenAll 的聚合异常若无人读，会在终结器线程上报为
            // UnobservedTaskException，把一次本已被吞掉的会话故障变成进程级噪声。
            _ = all.Exception;
        }

        return CountUnfinished(tasks);
    }

    private static int CountUnfinished(IReadOnlyList<Task> tasks)
    {
        var unfinished = 0;
        foreach (var task in tasks)
        {
            if (!task.IsCompleted)
            {
                unfinished++;
            }
        }

        return unfinished;
    }
}
