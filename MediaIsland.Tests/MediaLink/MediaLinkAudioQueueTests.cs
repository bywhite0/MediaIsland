using MediaIsland.Services.MediaLink;
using Xunit;

namespace MediaIsland.Tests.MediaLink;

/// <summary>
/// 音频出站队列。与 MediaLinkOutboundQueue 的策略刻意相反：满时无条件丢最旧，
/// 且入队永不失败。理由是消费场景不同——可视化只关心"现在在响什么"，
/// 积压的历史帧已经过期，保留最新帧才对；连续性由接收端环形缓冲负责。
/// </summary>
public class MediaLinkAudioQueueTests
{
    private static byte[] Frame(byte tag) => [tag];

    [Fact]
    public void UnderCapacity_PreservesFifoOrder()
    {
        var queue = new MediaLinkAudioQueue(4);
        queue.Enqueue(Frame(1));
        queue.Enqueue(Frame(2));
        queue.Enqueue(Frame(3));

        Assert.Equal([1, 2, 3], Drain(queue));
    }

    [Fact]
    public void Full_EvictsOldest_KeepsNewest()
    {
        var queue = new MediaLinkAudioQueue(3);
        queue.Enqueue(Frame(1));
        queue.Enqueue(Frame(2));
        queue.Enqueue(Frame(3));

        queue.Enqueue(Frame(4));

        Assert.Equal([2, 3, 4], Drain(queue));
    }

    [Fact]
    public void Enqueue_NeverFails_EvenWhenFull()
    {
        // 音频入队没有失败路径：满了就丢最旧，绝不触发 rate_limited 关闭会话。
        var queue = new MediaLinkAudioQueue(2);
        for (var i = 0; i < 100; i++)
        {
            queue.Enqueue(Frame((byte)i));
        }

        Assert.Equal(2, queue.Count);
    }

    [Fact]
    public void RepeatedOverflow_KeepsExactlyCapacityNewestFrames()
    {
        var queue = new MediaLinkAudioQueue(8);
        for (byte i = 0; i < 50; i++)
        {
            queue.Enqueue(Frame(i));
        }

        Assert.Equal([42, 43, 44, 45, 46, 47, 48, 49], Drain(queue));
    }

    [Fact]
    public void DroppedCount_TracksEvictions()
    {
        // 丢帧数要可观测，否则背压问题在生产环境无法诊断。
        var queue = new MediaLinkAudioQueue(2);
        queue.Enqueue(Frame(1));
        queue.Enqueue(Frame(2));
        Assert.Equal(0, queue.DroppedCount);

        queue.Enqueue(Frame(3));
        queue.Enqueue(Frame(4));

        Assert.Equal(2, queue.DroppedCount);
    }

    [Fact]
    public async Task DequeueAsync_ReturnsNull_AfterCompleteAndDrained()
    {
        var queue = new MediaLinkAudioQueue(4);
        queue.Enqueue(Frame(7));
        queue.Complete();

        Assert.Equal([7], (await queue.DequeueAsync(CancellationToken.None))!);
        Assert.Null(await queue.DequeueAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Complete_WakesWaitingDequeue()
    {
        var queue = new MediaLinkAudioQueue(4);
        var pending = queue.DequeueAsync(CancellationToken.None);
        Assert.False(pending.IsCompleted);

        queue.Complete();

        Assert.Null(await pending.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Enqueue_AfterComplete_Discards()
    {
        var queue = new MediaLinkAudioQueue(4);
        queue.Complete();

        queue.Enqueue(Frame(1));

        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void TryDequeue_EmptyQueue_ReturnsFalse()
    {
        var queue = new MediaLinkAudioQueue(4);

        Assert.False(queue.TryDequeue(out _));
    }

    [Fact]
    public async Task AudioQueue_EnqueueWakesWaitingDequeue()
    {
        // 变异测试证明：没有这条，注释掉 Enqueue 里的 _signal.Release() 也不会有测试失败，
        // 而音频发送泵正是靠它被唤醒——缺了它整条推送链路会静默停摆。
        var queue = new MediaLinkAudioQueue(4);
        var pending = queue.DequeueAsync(CancellationToken.None);
        Assert.False(pending.IsCompleted);

        queue.Enqueue([42]);

        var frame = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal([42], frame);
    }

    [Fact]
    public async Task AudioQueue_DequeueAsync_HonorsCancellation()
    {
        // 会话结束时写者任务靠取消令牌退出；不响应取消会让任务泄漏。
        var queue = new MediaLinkAudioQueue(4);
        using var cts = new CancellationTokenSource();
        var pending = queue.DequeueAsync(cts.Token);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    private static List<byte> Drain(MediaLinkAudioQueue queue)
    {
        var result = new List<byte>();
        while (queue.TryDequeue(out var frame))
        {
            result.Add(frame[0]);
        }

        return result;
    }
}
