using MediaIsland.Services.MediaLink;
using MediaIsland.Services.MediaLink.Mapping;
using MediaIsland.Services.MediaLink.Protocol;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using Xunit;

namespace MediaIsland.Tests.MediaLink;

/// <summary>
/// 客户端协议状态机。用可编排的假 socket 驱动，使握手顺序、seq 过滤与
/// epoch 重置成为确定性行为，不依赖真实网络时序。
/// </summary>
public class MediaLinkClientTests
{
    [Fact]
    public async Task Handshake_SendsAuthThenSubscribe_InOrder()
    {
        var socket = new ScriptedClientSocket();
        socket.QueueServerHello(epoch: 1);
        socket.QueueMessage(MediaLinkProtocol.TypeAuthOk);
        await using var client = NewClient(socket);

        client.Start();
        await socket.WaitForSendsAsync(2);

        var sent = socket.Sent.Select(Parse).ToArray();
        Assert.Equal(MediaLinkProtocol.TypeAuth, sent[0].Type);
        Assert.Equal(MediaLinkProtocol.TypeSubscribe, sent[1].Type);

        // Token 必须原样送出，否则服务端一律拒绝
        var auth = MediaLinkMessageSerializer.DeserializePayload<MediaLinkAuthPayload>(sent[0].Payload);
        Assert.Equal("tok", auth!.Token);
    }

    [Fact]
    public async Task AuthFail_DoesNotSubscribe()
    {
        var socket = new ScriptedClientSocket();
        socket.QueueServerHello(epoch: 1);
        socket.QueueMessage(MediaLinkProtocol.TypeAuthFail);
        await using var client = NewClient(socket);

        client.Start();
        await socket.WaitForSendsAsync(1);
        await Task.Delay(200);

        Assert.DoesNotContain(socket.Sent.Select(Parse), m => m.Type == MediaLinkProtocol.TypeSubscribe);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task MediaUpdated_RaisesEventWithDto()
    {
        var socket = new ScriptedClientSocket();
        socket.QueueServerHello(epoch: 1);
        socket.QueueMessage(MediaLinkProtocol.TypeAuthOk);
        socket.QueueMediaUpdated(seq: 1, title: "Song");
        await using var client = NewClient(socket);

        MediaLinkMediaDto? received = null;
        var signal = new TaskCompletionSource();
        client.MediaReceived += (_, e) => { received = e.Media; signal.TrySetResult(); };

        client.Start();
        await signal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("Song", received!.Title);
    }

    [Fact]
    public async Task StaleSeq_IsDiscarded()
    {
        var socket = new ScriptedClientSocket();
        socket.QueueServerHello(epoch: 1);
        socket.QueueMessage(MediaLinkProtocol.TypeAuthOk);
        socket.QueueMediaUpdated(seq: 5, title: "New");
        socket.QueueMediaUpdated(seq: 3, title: "Stale");   // 乱序到达
        socket.QueueMediaUpdated(seq: 5, title: "Dup");     // 重复
        socket.QueueMediaUpdated(seq: 6, title: "Newer");
        await using var client = NewClient(socket);

        var titles = new ConcurrentQueue<string>();
        client.MediaReceived += (_, e) => titles.Enqueue(e.Media.Title!);

        client.Start();
        await WaitUntilAsync(() => titles.Count >= 2);
        await Task.Delay(200); // 给被丢弃的帧留出出现的机会

        Assert.Equal(["New", "Newer"], titles);
    }

    [Fact]
    public async Task EpochChange_ResetsSeqWatermark()
    {
        // 服务端重建监听器后 seq 从 0 重新开始。不跟着 epoch 重置水位，
        // 客户端会永久停止更新——这是最容易漏掉的一条。
        var socket = new ScriptedClientSocket();
        socket.QueueServerHello(epoch: 1);
        socket.QueueMessage(MediaLinkProtocol.TypeAuthOk);
        socket.QueueMediaUpdated(seq: 100, title: "Before");
        socket.QueueServerHello(epoch: 2);                  // 重启
        socket.QueueMediaUpdated(seq: 1, title: "After");   // seq 归零
        await using var client = NewClient(socket);

        var titles = new ConcurrentQueue<string>();
        client.MediaReceived += (_, e) => titles.Enqueue(e.Media.Title!);

        client.Start();
        await WaitUntilAsync(() => titles.Count >= 2);

        Assert.Equal(["Before", "After"], titles);
    }

    [Fact]
    public async Task SameEpoch_DoesNotResetWatermark()
    {
        var socket = new ScriptedClientSocket();
        socket.QueueServerHello(epoch: 7);
        socket.QueueMessage(MediaLinkProtocol.TypeAuthOk);
        socket.QueueMediaUpdated(seq: 100, title: "First");
        socket.QueueServerHello(epoch: 7);                 // 同一 epoch 重发
        socket.QueueMediaUpdated(seq: 50, title: "Stale");
        await using var client = NewClient(socket);

        var titles = new ConcurrentQueue<string>();
        client.MediaReceived += (_, e) => titles.Enqueue(e.Media.Title!);

        client.Start();
        await WaitUntilAsync(() => titles.Count >= 1);
        await Task.Delay(200);

        Assert.Equal(["First"], titles);
    }

    [Fact]
    public async Task LyricsUpdated_RaisesEvent()
    {
        var socket = new ScriptedClientSocket();
        socket.QueueServerHello(epoch: 1);
        socket.QueueMessage(MediaLinkProtocol.TypeAuthOk);
        socket.QueueLyricsUpdated(seq: 1, trackToken: "abc");
        await using var client = NewClient(socket);

        MediaLinkLyricsDto? received = null;
        var signal = new TaskCompletionSource();
        client.LyricsReceived += (_, e) => { received = e.Lyrics; signal.TrySetResult(); };

        client.Start();
        await signal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("abc", received!.TrackToken);
    }

    [Fact]
    public async Task ConnectionDrop_Reconnects()
    {
        var socket = new ScriptedClientSocket();
        socket.QueueServerHello(epoch: 1);
        socket.QueueMessage(MediaLinkProtocol.TypeAuthOk);
        socket.QueueClose();                    // 对端断开
        socket.QueueServerHello(epoch: 1);      // 第二轮
        socket.QueueMessage(MediaLinkProtocol.TypeAuthOk);
        await using var client = NewClient(socket, retry: TimeSpan.FromMilliseconds(50));

        client.Start();
        await WaitUntilAsync(() => socket.ConnectCount >= 2);

        Assert.True(socket.ConnectCount >= 2);
    }

    [Fact]
    public async Task MalformedJson_DoesNotKillSession()
    {
        var socket = new ScriptedClientSocket();
        socket.QueueServerHello(epoch: 1);
        socket.QueueMessage(MediaLinkProtocol.TypeAuthOk);
        socket.QueueRaw("{ not json");
        socket.QueueMediaUpdated(seq: 1, title: "Survived");
        await using var client = NewClient(socket);

        var signal = new TaskCompletionSource<string>();
        client.MediaReceived += (_, e) => signal.TrySetResult(e.Media.Title!);

        client.Start();

        Assert.Equal("Survived", await signal.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ReceivedMedia_CarriesTickForAgeComputation()
    {
        // 转发需要"收帧时刻"才能算出 positionAgeMs，客户端必须把它交出来。
        var tick = 5_000L;
        var socket = new ScriptedClientSocket();
        socket.QueueServerHello(epoch: 1);
        socket.QueueMessage(MediaLinkProtocol.TypeAuthOk);
        socket.QueueMediaUpdated(seq: 1, title: "Song");
        await using var client = new MediaLinkClient(
            NewOptions(socket), logger: null, tickProvider: () => tick);

        var signal = new TaskCompletionSource<long>();
        client.MediaReceived += (_, e) => signal.TrySetResult(e.ReceivedAtTick);

        client.Start();

        Assert.Equal(5_000L, await signal.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static MediaLinkMessage Parse(string json) =>
        MediaLinkMessageSerializer.Deserialize(json)!;

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }

    private static MediaLinkClientOptions NewOptions(ScriptedClientSocket socket, TimeSpan? retry = null) => new()
    {
        Endpoint = new Uri("ws://127.0.0.1:1/v1/ws"),
        Token = "tok",
        InitialRetryDelay = retry ?? TimeSpan.FromMilliseconds(50),
        MaxRetryDelay = TimeSpan.FromMilliseconds(200),
        SocketFactory = () => socket
    };

    private static MediaLinkClient NewClient(ScriptedClientSocket socket, TimeSpan? retry = null) =>
        new(NewOptions(socket, retry));
}

/// <summary>
/// 按脚本回放服务端消息的假 socket。SocketFactory 每轮都返回同一实例，
/// 故重连时会接着消费剩余脚本——这正好用来测试重连。
/// </summary>
internal sealed class ScriptedClientSocket : IMediaLinkClientSocket
{
    private readonly ConcurrentQueue<string?> _inbound = new();
    private readonly ConcurrentQueue<string> _sent = new();
    private readonly SemaphoreSlim _inboundSignal = new(0);
    private readonly SemaphoreSlim _sentSignal = new(0);

    public WebSocketState State { get; private set; } = WebSocketState.None;

    public int ConnectCount { get; private set; }

    public IReadOnlyList<string> Sent => _sent.ToArray();

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        ConnectCount++;
        State = WebSocketState.Open;
        return Task.CompletedTask;
    }

    public Task SendTextAsync(string text, CancellationToken cancellationToken)
    {
        _sent.Enqueue(text);
        _sentSignal.Release();
        return Task.CompletedTask;
    }

    public async Task<MediaLinkSocketMessage> ReceiveAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (_inbound.TryDequeue(out var text))
            {
                // null 表示对端关闭：文本与二进制皆为 null 即 IsClosed。
                return new MediaLinkSocketMessage(text, null);
            }

            await _inboundSignal.WaitAsync(cancellationToken);
        }
    }

    public async Task WaitForSendsAsync(int count)
    {
        for (var i = 0; i < count; i++)
        {
            await _sentSignal.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    public void QueueRaw(string text)
    {
        _inbound.Enqueue(text);
        _inboundSignal.Release();
    }

    /// <summary>入队关闭信号，使 ReceiveTextAsync 返回 null。</summary>
    public void QueueClose()
    {
        _inbound.Enqueue(null);
        _inboundSignal.Release();
    }

    public void QueueMessage(string type) =>
        QueueRaw(MediaLinkMessageSerializer.Serialize(MediaLinkMessageSerializer.Create(type)));

    public void QueueServerHello(long epoch) =>
        QueueRaw(MediaLinkMessageSerializer.Serialize(MediaLinkMessageSerializer.Create(
            MediaLinkProtocol.TypeEvent,
            new MediaLinkServerHelloPayload
            {
                ProtocolVersion = 1,
                AuthRequired = true,
                SessionEpoch = epoch
            },
            name: MediaLinkProtocol.EventServerHello)));

    public void QueueMediaUpdated(long seq, string title) =>
        QueueRaw(MediaLinkMessageSerializer.Serialize(MediaLinkMessageSerializer.Create(
            MediaLinkProtocol.TypeEvent,
            new MediaLinkMediaDto
            {
                ChangeKind = "MediaProperties",
                SourceApp = "upstream",
                Title = title,
                PlaybackState = "Playing",
                PlaybackRate = 1.0
            },
            name: MediaLinkProtocol.EventMediaUpdated,
            seq: seq)));

    public void QueueLyricsUpdated(long seq, string trackToken) =>
        QueueRaw(MediaLinkMessageSerializer.Serialize(MediaLinkMessageSerializer.Create(
            MediaLinkProtocol.TypeEvent,
            new MediaLinkLyricsDto
            {
                Id = "ly",
                Title = "T",
                Artist = "A",
                Source = "Mock",
                TrackToken = trackToken,
                Document = new MediaLinkLyricsDocumentDto { Lines = [] }
            },
            name: MediaLinkProtocol.EventLyricsUpdated,
            seq: seq)));

    public ValueTask DisposeAsync()
    {
        State = WebSocketState.Closed;
        return ValueTask.CompletedTask;
    }
}

/// <summary>转发映射：positionAgeMs 的计算是跨实例链路的正确性关键。</summary>
public class MediaLinkForwardMappingTests
{
    [Fact]
    public void ToInjectPayload_SumsCaptureLagAndLocalElapsed()
    {
        var dto = new MediaLinkMediaDto
        {
            SourceApp = "upstream",
            Title = "Song",
            PositionMs = 30_000,
            DurationMs = 180_000,
            PlaybackState = "Playing",
            PlaybackRate = 1.0,
            PositionCapturedAtMs = 1_000_000,
            ServerTimeMs = 1_000_050   // 服务端内部延迟 50ms
        };

        var payload = MediaLinkDtoMapper.ToInjectPayload(dto, elapsedSinceReceiveMs: 120);

        Assert.Equal(170, payload.PositionAgeMs);   // 50 + 120
        Assert.Equal(30_000, payload.PositionMs);
        Assert.Equal("Song", payload.Title);
    }

    [Fact]
    public void ToInjectPayload_MissingTimeBase_FallsBackToLocalElapsedOnly()
    {
        // 上游未携带时间基准（旧版本服务端）时，只能补本地这一段。
        var dto = new MediaLinkMediaDto
        {
            SourceApp = "upstream",
            Title = "Song",
            PositionMs = 1_000,
            PlaybackState = "Playing"
        };

        var payload = MediaLinkDtoMapper.ToInjectPayload(dto, elapsedSinceReceiveMs: 80);

        Assert.Equal(80, payload.PositionAgeMs);
    }

    [Fact]
    public void ToInjectPayload_NegativeInputs_ClampToZero()
    {
        // 时钟回拨可能让差值为负；负的"年龄"没有意义。
        var dto = new MediaLinkMediaDto
        {
            SourceApp = "upstream",
            Title = "Song",
            PlaybackState = "Playing",
            PositionCapturedAtMs = 1_000_100,
            ServerTimeMs = 1_000_000   // 负的采样延迟
        };

        var payload = MediaLinkDtoMapper.ToInjectPayload(dto, elapsedSinceReceiveMs: -5);

        Assert.Equal(0, payload.PositionAgeMs);
    }
}
