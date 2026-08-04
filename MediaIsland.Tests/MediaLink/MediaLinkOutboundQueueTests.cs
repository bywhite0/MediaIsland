using System.Net.WebSockets;
using MediaIsland.Services.MediaLink;
using MediaIsland.Services.MediaLink.Protocol;
using Xunit;

namespace MediaIsland.Tests.MediaLink;

/// <summary>
/// 出站队列的丢弃策略。这里不启动写者任务，队列始终处于满载状态，
/// 使"丢哪一帧"成为确定性行为而非与排空速度赛跑。
/// </summary>
public class MediaLinkOutboundQueueTests
{
    [Fact]
    public void Full_DroppableArrives_EvictsOldestDroppable_NotHead()
    {
        // 队头是不可丢的歌词帧。Channel 的 DropOldest 会丢掉它——这正是要防的。
        var queue = new MediaLinkOutboundQueue(3);
        Assert.True(queue.TryEnqueue(new MediaLinkOutboundFrame("lyrics", Droppable: false)));
        Assert.True(queue.TryEnqueue(new MediaLinkOutboundFrame("media-1", Droppable: true)));
        Assert.True(queue.TryEnqueue(new MediaLinkOutboundFrame("media-2", Droppable: true)));

        Assert.True(queue.TryEnqueue(new MediaLinkOutboundFrame("media-3", Droppable: true)));

        Assert.Equal(["lyrics", "media-2", "media-3"], Drain(queue));
    }

    [Fact]
    public void Full_NonDroppableArrives_RejectsInsteadOfEvicting()
    {
        // 歌词不可丢：队列满时不得牺牲任何帧，交由调用方以 rate_limited 关闭会话。
        var queue = new MediaLinkOutboundQueue(2);
        queue.TryEnqueue(new MediaLinkOutboundFrame("media-1", Droppable: true));
        queue.TryEnqueue(new MediaLinkOutboundFrame("media-2", Droppable: true));

        Assert.False(queue.TryEnqueue(new MediaLinkOutboundFrame("lyrics", Droppable: false)));
        Assert.Equal(["media-1", "media-2"], Drain(queue));
    }

    [Fact]
    public void Full_AllNonDroppable_DroppableArrives_Rejected()
    {
        // 无可牺牲对象时，可丢帧自身也入不了队，而不是挤掉不可丢的帧。
        var queue = new MediaLinkOutboundQueue(2);
        queue.TryEnqueue(new MediaLinkOutboundFrame("ctrl-1", Droppable: false));
        queue.TryEnqueue(new MediaLinkOutboundFrame("ctrl-2", Droppable: false));

        Assert.False(queue.TryEnqueue(new MediaLinkOutboundFrame("media", Droppable: true)));
        Assert.Equal(["ctrl-1", "ctrl-2"], Drain(queue));
    }

    [Fact]
    public void Eviction_PreservesArrivalOrderOfSurvivors()
    {
        // 丢弃不得打乱其余帧的顺序，否则 seq 单调性被破坏。
        var queue = new MediaLinkOutboundQueue(4);
        queue.TryEnqueue(new MediaLinkOutboundFrame("m1", Droppable: true));
        queue.TryEnqueue(new MediaLinkOutboundFrame("ctrl", Droppable: false));
        queue.TryEnqueue(new MediaLinkOutboundFrame("m2", Droppable: true));
        queue.TryEnqueue(new MediaLinkOutboundFrame("m3", Droppable: true));

        queue.TryEnqueue(new MediaLinkOutboundFrame("m4", Droppable: true));

        Assert.Equal(["ctrl", "m2", "m3", "m4"], Drain(queue));
    }

    [Fact]
    public void RepeatedOverflow_NeverEvictsNonDroppable()
    {
        var queue = new MediaLinkOutboundQueue(8);
        queue.TryEnqueue(new MediaLinkOutboundFrame("keep-me", Droppable: false));
        for (var i = 0; i < 500; i++)
        {
            Assert.True(queue.TryEnqueue(new MediaLinkOutboundFrame($"m{i}", Droppable: true)));
        }

        var drained = Drain(queue);
        Assert.Equal(8, drained.Count);
        Assert.Equal("keep-me", drained[0]);
    }

    [Fact]
    public async Task DequeueAsync_ReturnsNull_AfterCompleteAndDrained()
    {
        var queue = new MediaLinkOutboundQueue(4);
        queue.TryEnqueue(new MediaLinkOutboundFrame("only", Droppable: false));
        queue.Complete();

        Assert.Equal("only", (await queue.DequeueAsync(CancellationToken.None))!.Value.Json);
        Assert.Null(await queue.DequeueAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Complete_WakesWaitingDequeue()
    {
        // 写者阻塞在空队列上时，Complete 必须唤醒它，否则停服会挂住。
        var queue = new MediaLinkOutboundQueue(4);
        var pending = queue.DequeueAsync(CancellationToken.None);
        Assert.False(pending.IsCompleted);

        queue.Complete();

        Assert.Null(await pending.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void TryEnqueue_AfterComplete_ReportsSuccessWithoutQueuing()
    {
        // 会话正在结束时的入队不是背压失败，不应触发 rate_limited 关闭。
        var queue = new MediaLinkOutboundQueue(2);
        queue.Complete();

        Assert.True(queue.TryEnqueue(new MediaLinkOutboundFrame("late", Droppable: false)));
        Assert.Equal(0, queue.Count);
    }

    private static List<string> Drain(MediaLinkOutboundQueue queue)
    {
        var result = new List<string>();
        while (queue.TryDequeue(out var frame))
        {
            result.Add(frame.Json);
        }

        return result;
    }
}

/// <summary>
/// 出站写超时。对端 TCP 窗口塞满时，无超时的写会连带 socket 锁挂住接收循环与写者任务，
/// 整个会话僵死且永不被回收。
/// </summary>
public class MediaLinkSendTimeoutTests
{
    [Fact]
    public async Task SendAsync_PeerNeverDrains_TimesOutAndMarksSessionClosed()
    {
        var socket = new HangingSocket();
        var session = new MediaLinkSession(socket, new MediaLinkSessionOptions { ExpectedToken = "t" });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await session.SendAsync(
            MediaLinkMessageSerializer.Create(MediaLinkProtocol.TypeEvent, name: "n"),
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));
        sw.Stop();

        // 5s 超时后返回，而不是永久挂起。
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"耗时 {sw.Elapsed}，疑似未超时");
        Assert.True(session.IsClosed);
    }

    [Fact]
    public async Task SendTimeout_DoesNotBlockSubsequentCallers()
    {
        // 超时会话必须释放 _sendLock，否则后续调用方会一起卡死在锁上。
        var socket = new HangingSocket();
        var session = new MediaLinkSession(socket, new MediaLinkSessionOptions { ExpectedToken = "t" });

        await session.SendAsync(
            MediaLinkMessageSerializer.Create(MediaLinkProtocol.TypeEvent, name: "first"),
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));

        // 会话已标记关闭，后续 SendAsync 应立即返回而非再等一个超时周期。
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await session.SendAsync(
            MediaLinkMessageSerializer.Create(MediaLinkProtocol.TypeEvent, name: "second"),
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"耗时 {sw.Elapsed}，疑似仍在等锁");
    }

    private sealed class HangingSocket : IMediaLinkSocket
    {
        public WebSocketState State { get; private set; } = WebSocketState.Open;

        public Task SendTextAsync(string text, CancellationToken cancellationToken) =>
            Task.Delay(Timeout.Infinite, cancellationToken);

        public Task<string?> ReceiveTextAsync(CancellationToken cancellationToken) =>
            Task.Delay(Timeout.Infinite, cancellationToken).ContinueWith(_ => (string?)null, cancellationToken);

        public Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken cancellationToken)
        {
            State = WebSocketState.Closed;
            return Task.CompletedTask;
        }
    }
}

/// <summary>
/// 会话数变更通知。设置页据此显示在线客户端数；此前该值只在 listener 重建时刷新，
/// 客户端连接/断开不会更新，界面长期停在 0。
/// </summary>
public class MediaLinkSessionCountNotificationTests
{
    [Fact]
    public void Add_RaisesSessionCountChanged()
    {
        var hub = new MediaLinkSessionHub();
        var hits = 0;
        hub.SessionCountChanged += () => hits++;

        hub.Add(NewSession());

        Assert.Equal(1, hits);
    }

    [Fact]
    public void Remove_RaisesSessionCountChanged()
    {
        var hub = new MediaLinkSessionHub();
        var session = NewSession();
        hub.Add(session);

        var hits = 0;
        hub.SessionCountChanged += () => hits++;
        hub.Remove(session);

        Assert.Equal(1, hits);
    }

    [Fact]
    public void Remove_UnknownSession_DoesNotRaise()
    {
        // 重复 Remove 不应产生虚假通知。
        var hub = new MediaLinkSessionHub();
        var hits = 0;
        hub.SessionCountChanged += () => hits++;

        hub.Remove(NewSession());

        Assert.Equal(0, hits);
    }

    [Fact]
    public void SessionCount_TracksAddAndRemove()
    {
        var hub = new MediaLinkSessionHub();
        var a = NewSession();
        var b = NewSession();

        hub.Add(a);
        hub.Add(b);
        Assert.Equal(2, hub.Sessions.Count);

        hub.Remove(a);
        Assert.Single(hub.Sessions);
    }

    private static MediaLinkSession NewSession() =>
        new(new FakeMediaLinkSocket(), new MediaLinkSessionOptions { ExpectedToken = "t" });
}
