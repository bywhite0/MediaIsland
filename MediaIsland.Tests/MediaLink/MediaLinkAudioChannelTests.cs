using System.Net.WebSockets;
using MediaIsland.Services.MediaLink;
using MediaIsland.Services.MediaLink.Protocol;
using Xunit;

namespace MediaIsland.Tests.MediaLink;

/// <summary>
/// 传输层的二进制收发。此前 WebSocketMediaLinkSocket 显式丢弃非 Text 帧，
/// 音频通道要求它能区分并分派两种帧类型。
/// </summary>
public class MediaLinkBinaryTransportTests
{
    [Fact]
    public async Task SendBinaryAsync_RecordedSeparatelyFromText()
    {
        var socket = new FakeMediaLinkSocket();

        await socket.SendTextAsync("hello", CancellationToken.None);
        await socket.SendBinaryAsync(new byte[] { 1, 2, 3 }, CancellationToken.None);

        Assert.Equal(["hello"], socket.Outgoing);
        Assert.Equal([new byte[] { 1, 2, 3 }], socket.OutgoingBinary);
    }

    [Fact]
    public async Task ReceiveAsync_YieldsTextMessage()
    {
        var socket = new FakeMediaLinkSocket();
        socket.EnqueueIncoming("payload");

        var message = await socket.ReceiveAsync(CancellationToken.None);

        Assert.Equal("payload", message.Text);
        Assert.Null(message.Binary);
    }

    [Fact]
    public async Task ReceiveAsync_YieldsBinaryMessage()
    {
        var socket = new FakeMediaLinkSocket();
        socket.EnqueueIncomingBinary([9, 8, 7]);

        var message = await socket.ReceiveAsync(CancellationToken.None);

        Assert.Null(message.Text);
        Assert.Equal(new byte[] { 9, 8, 7 }, message.Binary);
    }

    [Fact]
    public async Task ReceiveAsync_PreservesInterleavedOrder()
    {
        // 文本与二进制共用一条接收流，顺序必须保持，否则控制指令与音频帧会错位。
        var socket = new FakeMediaLinkSocket();
        socket.EnqueueIncoming("a");
        socket.EnqueueIncomingBinary([1]);
        socket.EnqueueIncoming("b");

        Assert.Equal("a", (await socket.ReceiveAsync(CancellationToken.None)).Text);
        Assert.Equal(new byte[] { 1 }, (await socket.ReceiveAsync(CancellationToken.None)).Binary);
        Assert.Equal("b", (await socket.ReceiveAsync(CancellationToken.None)).Text);
    }

    [Fact]
    public async Task ReceiveTextAsync_SkipsBinaryFrames()
    {
        // 既有调用方只关心文本；二进制帧不应让它们收到 null（那意味着连接关闭）。
        var socket = new FakeMediaLinkSocket();
        socket.EnqueueIncomingBinary([1, 2]);
        socket.EnqueueIncoming("text-after-binary");

        // ReceiveTextAsync 是接口默认实现，C# 只允许经接口引用调用，故此处显式转型。
        IMediaLinkSocket asSocket = socket;
        Assert.Equal("text-after-binary", await asSocket.ReceiveTextAsync(CancellationToken.None));
    }
}

/// <summary>
/// 会话级音频通道。核心不变量：未订阅 audio 的客户端行为与本功能上线前完全一致。
/// </summary>
public class MediaLinkAudioChannelTests
{
    private static byte[] SampleFrame(string trackToken = "tk1", uint seq = 1) =>
        MediaLinkAudioFrame.Encode(
            new MediaLinkAudioFrameHeader(0, 0, 0, seq, trackToken, MediaLinkAudioFrameFlags.None),
            [0x01, 0x02]);

    private static async Task<(MediaLinkSession Session, FakeMediaLinkSocket Socket, Task Run)> StartAuthenticatedAsync(
        MediaLinkSessionOptions? options = null)
    {
        var socket = new FakeMediaLinkSocket();
        var session = new MediaLinkSession(socket, options ?? new MediaLinkSessionOptions { ExpectedToken = "t" });
        session.StartWriter(CancellationToken.None);
        var run = session.RunAsync(CancellationToken.None);

        socket.EnqueueIncoming(
            """{"type":"auth","id":"a1","v":1,"ts":0,"payload":{"token":"t"}}""");
        await WaitUntilAsync(() => session.IsAuthenticated);
        socket.ClearOutgoing();
        return (session, socket, run);
    }

    private static async Task SubscribeAsync(FakeMediaLinkSocket socket, MediaLinkSession session, string channels)
    {
        // 用 $$$ 而非 $$：结尾的 }} 是两层 JSON 对象的字面闭合，与 $$ 的插值终止符冲突。
        socket.EnqueueIncoming(
            $$$"""{"type":"subscribe","id":"s1","v":1,"ts":0,"payload":{"channels":[{{{channels}}}]}}""");
        await WaitUntilAsync(() => socket.Outgoing.Any(o => o.Contains("subscribe_ok")));
        socket.ClearOutgoing();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        throw new TimeoutException("条件未在 5 秒内满足");
    }

    [Fact]
    public async Task SubscribedToAudio_ReceivesBinaryFrame()
    {
        var (session, socket, _) = await StartAuthenticatedAsync();
        await SubscribeAsync(socket, session, "\"audio\"");

        Assert.True(session.IsSubscribedToAudio);
        await session.EnqueueAudioAsync(SampleFrame(), CancellationToken.None);

        await WaitUntilAsync(() => socket.OutgoingBinary.Count == 1);
        Assert.True(MediaLinkAudioFrame.TryDecode(
            socket.OutgoingBinary.First(), out var header, out _, out _));
        Assert.Equal("tk1", header.TrackToken);
    }

    [Fact]
    public async Task NotSubscribedToAudio_ReceivesNothing()
    {
        var (session, socket, _) = await StartAuthenticatedAsync();
        await SubscribeAsync(socket, session, "\"media\",\"lyrics\"");

        Assert.False(session.IsSubscribedToAudio);
        await session.EnqueueAudioAsync(SampleFrame(), CancellationToken.None);

        await Task.Delay(200);
        Assert.Empty(socket.OutgoingBinary);
    }

    [Fact]
    public async Task LegacyClient_SubscribeMediaAndLyrics_Succeeds()
    {
        // 回归锁定：老客户端的订阅路径完全不受影响。
        var (session, socket, _) = await StartAuthenticatedAsync();

        socket.EnqueueIncoming(
            """{"type":"subscribe","id":"s1","v":1,"ts":0,"payload":{"channels":["media","lyrics"]}}""");

        await WaitUntilAsync(() => socket.Outgoing.Any(o => o.Contains("subscribe_ok")));
        Assert.DoesNotContain(socket.Outgoing, o => o.Contains("bad_request"));
    }

    [Fact]
    public async Task Unsubscribe_Audio_StopsPushing()
    {
        var (session, socket, _) = await StartAuthenticatedAsync();
        await SubscribeAsync(socket, session, "\"media\",\"audio\"");

        socket.EnqueueIncoming(
            """{"type":"unsubscribe","id":"u1","v":1,"ts":0,"payload":{"channels":["audio"]}}""");
        await WaitUntilAsync(() => socket.Outgoing.Any(o => o.Contains("unsubscribe_ok")));

        Assert.False(session.IsSubscribedToAudio);
        await session.EnqueueAudioAsync(SampleFrame(), CancellationToken.None);

        await Task.Delay(200);
        Assert.Empty(socket.OutgoingBinary);
    }

    [Fact]
    public async Task AudioPlayStart_BeforeAuth_ReturnsUnauthorized()
    {
        var socket = new FakeMediaLinkSocket();
        var session = new MediaLinkSession(socket, new MediaLinkSessionOptions { ExpectedToken = "t" });
        session.StartWriter(CancellationToken.None);
        _ = session.RunAsync(CancellationToken.None);

        socket.EnqueueIncoming("""{"type":"audio.play_start","id":"p1","v":1,"ts":0}""");

        await WaitUntilAsync(() => socket.Outgoing.Any(o => o.Contains("unauthorized")));
    }

    [Fact]
    public async Task AudioPlayStart_RecordsIntentAndSignalsHost()
    {
        // 语义变更（第 2 期）：原本断言 OnAudioPlayStartAsync 被调用一次。
        // 采集需求改为状态重算后不再有 start/stop 事件对，宿主收到的是「请重算」信号，
        // 真正的需求由 WantsAudioCapture 表达。生命周期的完整覆盖见 MediaLinkAudioLifecycleTests。
        var signals = 0;
        var options = new MediaLinkSessionOptions
        {
            ExpectedToken = "t",
            OnAudioCaptureDemandChangedAsync = () => { Interlocked.Increment(ref signals); return Task.CompletedTask; }
        };
        var (session, socket, _) = await StartAuthenticatedAsync(options);
        await SubscribeAsync(socket, session, "\"audio\"");

        socket.EnqueueIncoming("""{"type":"audio.play_start","id":"p1","v":1,"ts":0}""");

        await WaitUntilAsync(() => session.WantsAudioCapture);
        Assert.True(Volatile.Read(ref signals) >= 1);
    }

    [Fact]
    public async Task AudioPlayStop_RevokesIntent()
    {
        // 语义变更（第 2 期）：原本断言 OnAudioPlayStopAsync 被调用一次。
        var (session, socket, _) = await StartAuthenticatedAsync();
        await SubscribeAsync(socket, session, "\"audio\"");
        socket.EnqueueIncoming("""{"type":"audio.play_start","id":"p1","v":1,"ts":0}""");
        await WaitUntilAsync(() => session.WantsAudioCapture);

        socket.EnqueueIncoming("""{"type":"audio.play_stop","id":"p2","v":1,"ts":0}""");

        await WaitUntilAsync(() => !session.WantsAudioCapture);
    }

    [Fact]
    public async Task IncomingAudioFrame_InvokesCallback()
    {
        MediaLinkAudioFrameHeader? received = null;
        var options = new MediaLinkSessionOptions
        {
            ExpectedToken = "t",
            OnAudioFrameAsync = (header, _) => { received = header; return Task.CompletedTask; }
        };
        var (_, socket, _) = await StartAuthenticatedAsync(options);

        socket.EnqueueIncomingBinary(SampleFrame("inbound", seq: 7));

        await WaitUntilAsync(() => received is not null);
        Assert.Equal("inbound", received!.Value.TrackToken);
        Assert.Equal(7u, received.Value.Seq);
    }

    [Fact]
    public async Task IncomingAudioFrame_BeforeAuth_Ignored()
    {
        MediaLinkAudioFrameHeader? received = null;
        var socket = new FakeMediaLinkSocket();
        var session = new MediaLinkSession(socket, new MediaLinkSessionOptions
        {
            ExpectedToken = "t",
            OnAudioFrameAsync = (header, _) => { received = header; return Task.CompletedTask; }
        });
        session.StartWriter(CancellationToken.None);
        _ = session.RunAsync(CancellationToken.None);

        socket.EnqueueIncomingBinary(SampleFrame());

        await Task.Delay(300);
        Assert.Null(received);
    }

    [Fact]
    public async Task MalformedAudioFrame_DoesNotCloseSession()
    {
        // 未知 magic 可能是未来版本的其他二进制帧，丢弃即可，不能断连。
        var (session, socket, _) = await StartAuthenticatedAsync();

        socket.EnqueueIncomingBinary([0xFF, 0xFF, 0xFF]);
        socket.EnqueueIncoming("""{"type":"ping","id":"p1","v":1,"ts":0}""");

        await WaitUntilAsync(() => socket.Outgoing.Any(o => o.Contains("pong")));
        Assert.False(session.IsClosed);
    }

    [Fact]
    public async Task AudioQueueOverflow_DropsFrames_WithoutClosingSession()
    {
        // 音频队列满不触发 rate_limited——这是与 JSON 队列相反的刻意设计。
        var (session, socket, _) = await StartAuthenticatedAsync();
        await SubscribeAsync(socket, session, "\"audio\"");

        for (uint i = 0; i < 200; i++)
        {
            await session.EnqueueAudioAsync(SampleFrame(seq: i), CancellationToken.None);
        }

        await Task.Delay(300);
        Assert.False(session.IsClosed);
        Assert.DoesNotContain(socket.Outgoing, o => o.Contains("rate_limited"));
    }

    [Fact]
    public async Task AudioDroppedCount_ExposesBackpressure()
    {
        // 丢帧刻意不触发 rate_limited，因此计数器是这条通道唯一的可观测信号。
        // 没有它，生产环境的背压问题无从诊断。
        var (session, socket, _) = await StartAuthenticatedAsync();
        await SubscribeAsync(socket, session, "\"audio\"");
        Assert.Equal(0, session.AudioDroppedCount);

        for (uint i = 0; i < 200; i++)
        {
            await session.EnqueueAudioAsync(SampleFrame(seq: i), CancellationToken.None);
        }

        await WaitUntilAsync(() => session.AudioDroppedCount > 0);
    }
}
