using System.Net.WebSockets;
using System.Collections.Concurrent;
using MediaIsland.Services.MediaLink;
using MediaIsland.Services.MediaLink.Protocol;
using Xunit;

namespace MediaIsland.Tests.MediaLink;

internal sealed class FakeMediaLinkSocket : IMediaLinkSocket
{
    private readonly ConcurrentQueue<MediaLinkSocketMessage> _incoming = new();
    private readonly SemaphoreSlim _incomingSignal = new(0);
    private readonly ConcurrentQueue<string> _outgoing = new();
    private readonly ConcurrentQueue<byte[]> _outgoingBinary = new();
    private int _closed;

    public WebSocketState State => Volatile.Read(ref _closed) == 0 ? WebSocketState.Open : WebSocketState.Closed;

    public IReadOnlyCollection<string> Outgoing => _outgoing.ToArray();

    public IReadOnlyCollection<byte[]> OutgoingBinary => _outgoingBinary.ToArray();

    public void ClearOutgoing()
    {
        while (_outgoing.TryDequeue(out _)) { }
        while (_outgoingBinary.TryDequeue(out _)) { }
    }

    public void EnqueueIncoming(string text)
    {
        _incoming.Enqueue(new MediaLinkSocketMessage(text, null));
        _incomingSignal.Release();
    }

    public void EnqueueIncomingBinary(byte[] data)
    {
        _incoming.Enqueue(new MediaLinkSocketMessage(null, data));
        _incomingSignal.Release();
    }

    /// <summary>
    /// 模拟对端断开：Text 与 Binary 皆为 null 即 IsClosed，接收循环据此退出。
    /// 用于验证「客户端进程被杀」这类不发关闭握手的路径。
    /// </summary>
    public void EnqueueClose()
    {
        _incoming.Enqueue(default);
        _incomingSignal.Release();
    }

    public Task SendTextAsync(string text, CancellationToken cancellationToken)
    {
        _outgoing.Enqueue(text);
        return Task.CompletedTask;
    }

    public Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        _outgoingBinary.Enqueue(data.ToArray());
        return Task.CompletedTask;
    }

    public async Task<MediaLinkSocketMessage> ReceiveAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (_incoming.TryDequeue(out var message))
            {
                return message;
            }

            await _incomingSignal.WaitAsync(cancellationToken);
        }
    }

    public Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _closed, 1);
        return Task.CompletedTask;
    }
}

public class MediaLinkSessionTests
{
    [Fact]
    public async Task UnauthenticatedSubscribe_SendsUnauthorizedAndCloses()
    {
        var socket = new FakeMediaLinkSocket();
        var session = new MediaLinkSession(socket, new MediaLinkSessionOptions { ExpectedToken = "secret" });

        await session.HandleMessageAsync(
            MediaLinkMessageSerializer.Serialize(MediaLinkMessageSerializer.Create(
                MediaLinkProtocol.TypeSubscribe,
                new MediaLinkSubscribePayload { Channels = [MediaLinkProtocol.ChannelMedia] },
                id: "1")),
            CancellationToken.None);

        Assert.Contains(socket.Outgoing, json =>
            json.Contains(MediaLinkProtocol.TypeError, StringComparison.Ordinal) &&
            json.Contains(MediaLinkProtocol.ErrorUnauthorized, StringComparison.Ordinal));
        Assert.Equal(WebSocketState.Closed, socket.State);
        Assert.False(session.IsAuthenticated);
    }

    [Fact]
    public async Task AuthSuccess_SendsAuthOk()
    {
        var socket = new FakeMediaLinkSocket();
        var session = new MediaLinkSession(socket, new MediaLinkSessionOptions { ExpectedToken = "secret" });

        await session.HandleMessageAsync(
            MediaLinkMessageSerializer.Serialize(MediaLinkMessageSerializer.Create(
                MediaLinkProtocol.TypeAuth,
                new MediaLinkAuthPayload { Token = "secret" },
                id: "a1")),
            CancellationToken.None);

        Assert.True(session.IsAuthenticated);
        Assert.Contains(socket.Outgoing, json => json.Contains(MediaLinkProtocol.TypeAuthOk, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AuthFail_Closes()
    {
        var socket = new FakeMediaLinkSocket();
        var session = new MediaLinkSession(socket, new MediaLinkSessionOptions { ExpectedToken = "secret" });

        await session.HandleMessageAsync(
            MediaLinkMessageSerializer.Serialize(MediaLinkMessageSerializer.Create(
                MediaLinkProtocol.TypeAuth,
                new MediaLinkAuthPayload { Token = "nope" })),
            CancellationToken.None);

        Assert.False(session.IsAuthenticated);
        Assert.Contains(socket.Outgoing, json => json.Contains(MediaLinkProtocol.TypeAuthFail, StringComparison.Ordinal));
        Assert.Equal(WebSocketState.Closed, socket.State);
    }

    [Fact]
    public async Task AuthThenSubscribe_InvokesSnapshotCallback()
    {
        var socket = new FakeMediaLinkSocket();
        var snapshotHits = 0;
        var session = new MediaLinkSession(socket, new MediaLinkSessionOptions
        {
            ExpectedToken = "secret",
            OnSubscribedAsync = _ =>
            {
                Interlocked.Increment(ref snapshotHits);
                return Task.CompletedTask;
            }
        });

        await session.HandleMessageAsync(
            MediaLinkMessageSerializer.Serialize(MediaLinkMessageSerializer.Create(
                MediaLinkProtocol.TypeAuth,
                new MediaLinkAuthPayload { Token = "secret" })),
            CancellationToken.None);
        await session.HandleMessageAsync(
            MediaLinkMessageSerializer.Serialize(MediaLinkMessageSerializer.Create(
                MediaLinkProtocol.TypeSubscribe,
                new MediaLinkSubscribePayload
                {
                    Channels = [MediaLinkProtocol.ChannelMedia, MediaLinkProtocol.ChannelLyrics]
                })),
            CancellationToken.None);

        Assert.Equal(1, snapshotHits);
        Assert.True(session.IsSubscribedTo(MediaLinkProtocol.ChannelMedia));
        Assert.True(session.IsSubscribedTo(MediaLinkProtocol.ChannelLyrics));
        Assert.Contains(socket.Outgoing, json => json.Contains(MediaLinkProtocol.TypeSubscribeOk, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Hub_BroadcastsOnlyToSubscribedChannel()
    {
        var mediaSocket = new FakeMediaLinkSocket();
        var lyricsSocket = new FakeMediaLinkSocket();
        var hub = new MediaLinkSessionHub();

        var mediaSession = new MediaLinkSession(mediaSocket, new MediaLinkSessionOptions { ExpectedToken = "t" });
        var lyricsSession = new MediaLinkSession(lyricsSocket, new MediaLinkSessionOptions { ExpectedToken = "t" });
        hub.Add(mediaSession);
        hub.Add(lyricsSession);
        mediaSession.StartWriter(CancellationToken.None);
        lyricsSession.StartWriter(CancellationToken.None);

        await mediaSession.HandleMessageAsync(AuthJson(), CancellationToken.None);
        await mediaSession.HandleMessageAsync(SubscribeJson(MediaLinkProtocol.ChannelMedia), CancellationToken.None);
        await lyricsSession.HandleMessageAsync(AuthJson(), CancellationToken.None);
        await lyricsSession.HandleMessageAsync(SubscribeJson(MediaLinkProtocol.ChannelLyrics), CancellationToken.None);

        mediaSocket.ClearOutgoing();
        lyricsSocket.ClearOutgoing();

        await hub.BroadcastEventAsync(
            MediaLinkProtocol.ChannelMedia,
            MediaLinkProtocol.EventMediaUpdated,
            new MediaLinkMediaDto { Title = "only-media" });
        await Task.Delay(100);

        Assert.NotEmpty(mediaSocket.Outgoing);
        Assert.Empty(lyricsSocket.Outgoing);
        Assert.Contains(mediaSocket.Outgoing, json => json.Contains("only-media", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unsubscribe_RemovesChannelAndSendsOk()
    {
        var socket = new FakeMediaLinkSocket();
        var session = new MediaLinkSession(socket, new MediaLinkSessionOptions { ExpectedToken = "t" });

        await session.HandleMessageAsync(AuthJson(), CancellationToken.None);
        await session.HandleMessageAsync(
            SubscribeJson(MediaLinkProtocol.ChannelMedia, MediaLinkProtocol.ChannelLyrics),
            CancellationToken.None);

        Assert.True(session.IsSubscribedTo(MediaLinkProtocol.ChannelMedia));
        Assert.True(session.IsSubscribedTo(MediaLinkProtocol.ChannelLyrics));

        socket.ClearOutgoing();
        await session.HandleMessageAsync(
            MediaLinkMessageSerializer.Serialize(MediaLinkMessageSerializer.Create(
                MediaLinkProtocol.TypeUnsubscribe,
                new MediaLinkUnsubscribePayload { Channels = [MediaLinkProtocol.ChannelLyrics] },
                id: "unsub-1")),
            CancellationToken.None);

        Assert.True(session.IsSubscribedTo(MediaLinkProtocol.ChannelMedia));
        Assert.False(session.IsSubscribedTo(MediaLinkProtocol.ChannelLyrics));
        Assert.Contains(socket.Outgoing, json =>
            json.Contains(MediaLinkProtocol.TypeUnsubscribeOk, StringComparison.Ordinal) &&
            json.Contains("unsub-1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unsubscribe_AllChannels_ThenReceivesNoBroadcasts()
    {
        var socket = new FakeMediaLinkSocket();
        var session = new MediaLinkSession(socket, new MediaLinkSessionOptions { ExpectedToken = "t" });
        var hub = new MediaLinkSessionHub();
        hub.Add(session);
        session.StartWriter(CancellationToken.None);

        await session.HandleMessageAsync(AuthJson(), CancellationToken.None);
        await session.HandleMessageAsync(SubscribeJson(MediaLinkProtocol.ChannelMedia), CancellationToken.None);

        socket.ClearOutgoing();
        await session.HandleMessageAsync(
            MediaLinkMessageSerializer.Serialize(MediaLinkMessageSerializer.Create(
                MediaLinkProtocol.TypeUnsubscribe,
                new MediaLinkUnsubscribePayload { Channels = [MediaLinkProtocol.ChannelMedia] },
                id: "unsub-all")),
            CancellationToken.None);

        Assert.False(session.IsSubscribedTo(MediaLinkProtocol.ChannelMedia));
        await Task.Delay(50);
        Assert.Contains(socket.Outgoing, json => json.Contains(MediaLinkProtocol.TypeUnsubscribeOk, StringComparison.Ordinal));

        socket.ClearOutgoing();
        await hub.BroadcastEventAsync(
            MediaLinkProtocol.ChannelMedia,
            MediaLinkProtocol.EventMediaUpdated,
            new MediaLinkMediaDto { Title = "should-not-arrive" });

        Assert.Empty(socket.Outgoing);
    }

    [Fact]
    public async Task Unsubscribe_NotSubscribed_SendsBadRequest()
    {
        var socket = new FakeMediaLinkSocket();
        var session = new MediaLinkSession(socket, new MediaLinkSessionOptions { ExpectedToken = "t" });

        await session.HandleMessageAsync(AuthJson(), CancellationToken.None);
        await session.HandleMessageAsync(SubscribeJson(MediaLinkProtocol.ChannelMedia), CancellationToken.None);

        socket.ClearOutgoing();
        await session.HandleMessageAsync(
            MediaLinkMessageSerializer.Serialize(MediaLinkMessageSerializer.Create(
                MediaLinkProtocol.TypeUnsubscribe,
                new MediaLinkUnsubscribePayload { Channels = [MediaLinkProtocol.ChannelLyrics] },
                id: "unsub-fail")),
            CancellationToken.None);

        Assert.Contains(socket.Outgoing, json =>
            json.Contains(MediaLinkProtocol.TypeError, StringComparison.Ordinal) &&
            json.Contains(MediaLinkProtocol.ErrorBadRequest, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unsubscribe_InvalidChannel_SendsBadRequest()
    {
        var socket = new FakeMediaLinkSocket();
        var session = new MediaLinkSession(socket, new MediaLinkSessionOptions { ExpectedToken = "t" });

        await session.HandleMessageAsync(AuthJson(), CancellationToken.None);

        socket.ClearOutgoing();
        await session.HandleMessageAsync(
            MediaLinkMessageSerializer.Serialize(MediaLinkMessageSerializer.Create(
                MediaLinkProtocol.TypeUnsubscribe,
                new MediaLinkUnsubscribePayload { Channels = ["nonexistent"] },
                id: "unsub-bad")),
            CancellationToken.None);

        Assert.Contains(socket.Outgoing, json =>
            json.Contains(MediaLinkProtocol.TypeError, StringComparison.Ordinal) &&
            json.Contains(MediaLinkProtocol.ErrorBadRequest, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unsubscribe_PreAuth_SendsUnauthorizedAndCloses()
    {
        var socket = new FakeMediaLinkSocket();
        var session = new MediaLinkSession(socket, new MediaLinkSessionOptions { ExpectedToken = "t" });

        await session.HandleMessageAsync(
            MediaLinkMessageSerializer.Serialize(MediaLinkMessageSerializer.Create(
                MediaLinkProtocol.TypeUnsubscribe,
                new MediaLinkUnsubscribePayload { Channels = [MediaLinkProtocol.ChannelMedia] },
                id: "unsub-preauth")),
            CancellationToken.None);

        Assert.Contains(socket.Outgoing, json =>
            json.Contains(MediaLinkProtocol.ErrorUnauthorized, StringComparison.Ordinal));
        Assert.Equal(WebSocketState.Closed, socket.State);
    }

        [Fact]
    public async Task QueueOverflow_DropsOldestMedia_WhenFull()
    {
        var socket = new FakeMediaLinkSocket();
        var session = new MediaLinkSession(socket, new MediaLinkSessionOptions { ExpectedToken = "t" });
        session.StartWriter(CancellationToken.None);

        await session.HandleMessageAsync(AuthJson(), CancellationToken.None);
        await session.HandleMessageAsync(
            MediaLinkMessageSerializer.Serialize(MediaLinkMessageSerializer.Create(
                MediaLinkProtocol.TypeSubscribe,
                new MediaLinkSubscribePayload { Channels = [MediaLinkProtocol.ChannelMedia, MediaLinkProtocol.ChannelLyrics] },
                id: "s1")),
            CancellationToken.None);

        // Enqueue many media.updated without draining -> oldest dropped, no crash
        for (var i = 0; i < 200; i++)
        {
            await session.EnqueueAsync(
                MediaLinkMessageSerializer.Create(MediaLinkProtocol.TypeEvent,
                    new MediaLinkMediaDto { Title = $"fill-{i}", SourceApp = "test", PlaybackState = "Playing", ChangeKind = "Timeline" },
                    name: MediaLinkProtocol.EventMediaUpdated,
                    seq: i),
                droppable: true);
        }

        // Enqueue lyrics.updated -> succeeds (media dropped to make room)
        await session.EnqueueAsync(
            MediaLinkMessageSerializer.Create(MediaLinkProtocol.TypeEvent,
                new MediaLinkLyricsDto { Id = "ly-1", Title = "Test", Artist = "A", DurationMs = 1000, Source = "External" },
                name: MediaLinkProtocol.EventLyricsUpdated),
            droppable: false);

        await Task.Delay(200);
        // Session should NOT be closed - media was dropped to make room
        Assert.False(session.IsClosed);
        // Writer should have processed some items
        Assert.NotEmpty(socket.Outgoing);
    }

    /// <summary>
    /// 满队且队列中有可丢帧时，不可丢帧必须挤掉最旧的可丢帧入队，而不是被拒、把会话关掉。
    ///
    /// 填充阶段刻意不启动写者：队列只进不出，「到底满没满」是确定的，不与排空速度赛跑。
    /// 塞完再起写者，断言那条 lyrics 确实发了出去——光断言「会话没被关」是单边的，
    /// 入队实现什么都不做也照样满足，证明不了那条帧真的活了下来。
    /// </summary>
    [Fact]
    public async Task QueueFull_NonDroppableFrame_EvictsMediaAndDeliversLyrics()
    {
        var socket = new SignalingSocket(MediaLinkProtocol.EventLyricsUpdated);
        var session = new MediaLinkSession(socket, new MediaLinkSessionOptions { ExpectedToken = "t" });

        // 远超队列容量，确保塞满之后队列里躺着的全是可丢帧。
        for (var i = 0; i < 200; i++)
        {
            await session.EnqueueAsync(
                MediaLinkMessageSerializer.Create(MediaLinkProtocol.TypeEvent,
                    new MediaLinkMediaDto { Title = $"fill-{i}", SourceApp = "test", PlaybackState = "Playing", ChangeKind = "Timeline" },
                    name: MediaLinkProtocol.EventMediaUpdated,
                    seq: i),
                droppable: true);
        }

        await session.EnqueueAsync(
            MediaLinkMessageSerializer.Create(MediaLinkProtocol.TypeEvent,
                new MediaLinkLyricsDto { Id = "ly-1", Title = "Test", Artist = "A", DurationMs = 1000, Source = "External" },
                name: MediaLinkProtocol.EventLyricsUpdated),
            droppable: false);

        Assert.False(session.IsClosed);
        Assert.Equal(WebSocketState.Open, socket.State);

        // 此刻帧已经躺在队列里，写者只会把它发出去——等的是必然结果，不是赛跑。
        // 超时只是断言失败时不挂死的护栏：正常路径上帧一发出即返回，不轮询也不睡眠。
        session.StartWriter(CancellationToken.None);
        await socket.Delivered.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// 记录出站文本帧，并在首次发出含指定片段的帧时完成 <see cref="Delivered"/>。
    /// 用事件而非轮询观测写者的产出：帧一上线就完成，无需 sleep，也没有轮询间隔。
    /// </summary>
    private sealed class SignalingSocket(string fragment) : IMediaLinkSocket
    {
        private readonly TaskCompletionSource _delivered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _closed;

        /// <summary>含指定片段的帧已发出。</summary>
        public Task Delivered => _delivered.Task;

        public WebSocketState State => Volatile.Read(ref _closed) == 0 ? WebSocketState.Open : WebSocketState.Closed;

        public Task SendTextAsync(string text, CancellationToken cancellationToken)
        {
            if (text.Contains(fragment, StringComparison.Ordinal))
            {
                _delivered.TrySetResult();
            }

            return Task.CompletedTask;
        }

        public Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        // 本测试不跑接收循环；永不完成而非抛出，避免将来被复用时炸在无关路径上。
        public Task<MediaLinkSocketMessage> ReceiveAsync(CancellationToken cancellationToken) =>
            Task.Delay(Timeout.Infinite, cancellationToken).ContinueWith(_ => default(MediaLinkSocketMessage), cancellationToken);

        public Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken cancellationToken)
        {
            Interlocked.Exchange(ref _closed, 1);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// 满队且一条可丢帧都没有：没有可牺牲的对象，入队必须失败并以 rate_limited 关闭会话。
    /// 这是「一律牺牲最旧可丢帧」的边界——不锁住它，把找牺牲者写成恒能找到也照样全绿。
    /// </summary>
    [Fact]
    public async Task QueueFull_AllNonDroppable_FailsEnqueueAndClosesSession()
    {
        var socket = new FakeMediaLinkSocket();
        var session = new MediaLinkSession(socket, new MediaLinkSessionOptions { ExpectedToken = "t" });

        // 全部不可丢，塞到远超容量：一旦满了就再没有能腾出来的位置。
        for (var i = 0; i < 200; i++)
        {
            await session.EnqueueAsync(
                MediaLinkMessageSerializer.Create(MediaLinkProtocol.TypeEvent,
                    new MediaLinkLyricsDto { Id = $"ly-{i}", Title = "Test", Artist = "A", DurationMs = 1000, Source = "External" },
                    name: MediaLinkProtocol.EventLyricsUpdated),
                droppable: false);
        }

        Assert.True(session.IsClosed);
        Assert.Equal(WebSocketState.Closed, socket.State);
    }

    /// <summary>
    /// 停服的后置条件：<c>DisposeAsync</c> 返回即该会话再无后台工作。
    ///
    /// 改动前两个写者任务全文无一处 await，Dispose 只 Complete 队列就返回。后果是在测试
    /// 进程里第 N 条用例的写者可以活进第 N+1 条——那是「随负载出现」的挂起的一个合理来源。
    ///
    /// 判据写成「在界内返回」的竞争形式而不是直接 await：等写者若变成无界，
    /// 直接 await 会让测试挂住而不是变红，那正是本期要消灭的形态在判据里重演。
    /// </summary>
    [Fact]
    public async Task DisposeAsync_DoesNotReturnWhileWritersAreStillRunning()
    {
        var socket = new FakeMediaLinkSocket();
        var session = new MediaLinkSession(socket, new MediaLinkSessionOptions { ExpectedToken = "t" });
        session.StartWriter(CancellationToken.None);

        // 地基：先钉住写者确实起来了。否则「都完成了」与「压根没起」长得一模一样。
        Assert.NotNull(session.WriterTaskForTest);
        Assert.NotNull(session.AudioWriterTaskForTest);

        var disposing = session.DisposeAsync().AsTask();
        var winner = await Task.WhenAny(disposing, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(ReferenceEquals(winner, disposing), "DisposeAsync 没有在 10s 内返回");
        await disposing;

        Assert.True(session.WriterTaskForTest!.IsCompleted, "DisposeAsync 返回时出站写者仍在飞");
        Assert.True(session.AudioWriterTaskForTest!.IsCompleted, "DisposeAsync 返回时音频写者仍在飞");
    }

    /// <summary>
    /// 排水期限必须从 <see cref="MediaLinkSession.SendTimeout"/> 推导。理由同服务端那条：
    /// 写者退出前最多还压着一次发送。字面量在 SendTimeout 被调大后不会跟。
    /// </summary>
    [Fact]
    public void WriterDrainTimeout_TracksSendTimeout()
    {
        Assert.Equal(
            MediaLinkSession.SendTimeout + TimeSpan.FromSeconds(1),
            MediaLinkSession.WriterDrainTimeout);
        Assert.True(MediaLinkSession.WriterDrainTimeout > MediaLinkSession.SendTimeout);
    }

private static string AuthJson() =>
        MediaLinkMessageSerializer.Serialize(MediaLinkMessageSerializer.Create(
            MediaLinkProtocol.TypeAuth,
            new MediaLinkAuthPayload { Token = "t" }));

    private static string SubscribeJson(params string[] channels) =>
        MediaLinkMessageSerializer.Serialize(MediaLinkMessageSerializer.Create(
            MediaLinkProtocol.TypeSubscribe,
            new MediaLinkSubscribePayload { Channels = channels.ToList() }));
}