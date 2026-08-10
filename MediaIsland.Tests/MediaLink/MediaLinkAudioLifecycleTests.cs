using System.Net.WebSockets;
using MediaIsland.Services.MediaLink;
using MediaIsland.Services.MediaLink.Protocol;
using Xunit;

namespace MediaIsland.Tests.MediaLink;

/// <summary>
/// 音频采集需求的生命周期。
///
/// 第 1 期把「是否该采集」建模成了增量事件对（OnAudioPlayStartAsync / OnAudioPlayStopAsync），
/// 于是每个 start 必须精确配对一个 stop。而会话的终结路径有五条——RunAsync 正常退出、
/// auth 超时关闭、rate_limited 关闭、CloseGoingAwayAsync、DisposeAllAsync——要求五条路径
/// 每条都记得补发 stop 是脆弱的：漏一条就永久占着音频设备且没有任何信号。
///
/// 现在改为状态函数：需要采集 ⟺ ∃ 会话 s：s 未关闭 ∧ 已认证 ∧ 订阅了 audio ∧ 请求过 play_start。
/// 宿主收到信号后重算而非增减，故任何路径漏触发最多延迟一拍，不会永久错。
///
/// 这里锁定的正是该性质——尤其是「杀掉客户端进程后采集必须停」这条。
/// </summary>
public class MediaLinkAudioLifecycleTests
{
    // ---- 夹具 ----

    private static byte[] SampleFrame(string trackToken = "tk1", uint seq = 1) =>
        MediaLinkAudioFrame.Encode(
            new MediaLinkAudioFrameHeader(0, 0, 0, seq, trackToken, MediaLinkAudioFrameFlags.None),
            [0x01, 0x02]);

    private static (MediaLinkSession Session, FakeMediaLinkSocket Socket) NewSession(
        MediaLinkSessionOptions? options = null)
    {
        var socket = new FakeMediaLinkSocket();
        var session = new MediaLinkSession(socket, options ?? new MediaLinkSessionOptions { ExpectedToken = "t" });
        session.StartWriter(CancellationToken.None);
        _ = session.RunAsync(CancellationToken.None);
        return (session, socket);
    }

    private static async Task<(MediaLinkSession Session, FakeMediaLinkSocket Socket)> StartAuthenticatedAsync(
        MediaLinkSessionOptions? options = null)
    {
        var (session, socket) = NewSession(options);
        socket.EnqueueIncoming("""{"type":"auth","id":"a1","v":1,"ts":0,"payload":{"token":"t"}}""");
        await WaitUntilAsync(() => session.IsAuthenticated);
        socket.ClearOutgoing();
        return (session, socket);
    }

    private static async Task SubscribeAsync(FakeMediaLinkSocket socket, string channels)
    {
        // 用 $$$ 而非 $$：结尾的 }} 是两层 JSON 对象的字面闭合，与 $$ 的插值终止符冲突。
        socket.EnqueueIncoming(
            $$$"""{"type":"subscribe","id":"s1","v":1,"ts":0,"payload":{"channels":[{{{channels}}}]}}""");
        await WaitUntilAsync(() => socket.Outgoing.Any(o => o.Contains("subscribe_ok")));
        socket.ClearOutgoing();
    }

    private static async Task UnsubscribeAsync(FakeMediaLinkSocket socket, string channels)
    {
        socket.EnqueueIncoming(
            $$$"""{"type":"unsubscribe","id":"u1","v":1,"ts":0,"payload":{"channels":[{{{channels}}}]}}""");
        await WaitUntilAsync(() => socket.Outgoing.Any(o => o.Contains("unsubscribe_ok")));
        socket.ClearOutgoing();
    }

    private static async Task PlayStartAsync(FakeMediaLinkSocket socket, string id = "p1")
    {
        socket.EnqueueIncoming($$"""{"type":"audio.play_start","id":"{{id}}","v":1,"ts":0}""");
        await WaitUntilAsync(() => socket.Outgoing.Any(o => o.Contains(id)));
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

    // ---- 需求状态量 ----

    [Fact]
    public async Task Authenticated_SubscribedAndPlayStarted_WantsCapture()
    {
        var (session, socket) = await StartAuthenticatedAsync();
        await SubscribeAsync(socket, "\"audio\"");
        await PlayStartAsync(socket);

        Assert.True(session.WantsAudioCapture);
    }

    [Fact]
    public async Task SubscribedWithoutPlayStart_DoesNotWantCapture()
    {
        // 光订阅不采集：订阅表达「愿意收」，play_start 才表达「现在要」。
        var (session, socket) = await StartAuthenticatedAsync();
        await SubscribeAsync(socket, "\"audio\"");

        Assert.False(session.WantsAudioCapture);
    }

    [Fact]
    public async Task PlayStartWithoutSubscription_DoesNotWantCapture()
    {
        // 台账第 3 条：未订阅 audio 也能 play_start 成功，宿主白占音频设备而帧全被丢。
        // 门禁落在需求函数而非应答——play_start 仍回 ok（见下方协议表面回归）。
        var (session, socket) = await StartAuthenticatedAsync();
        await SubscribeAsync(socket, "\"media\",\"lyrics\"");
        await PlayStartAsync(socket);

        Assert.False(session.WantsAudioCapture);
    }

    [Fact]
    public async Task NotAuthenticated_DoesNotWantCapture()
    {
        // 台账第 4 条：IsSubscribedToAudio 原本不查 _authenticated，与 IsSubscribedTo 语义不一致。
        var (session, _) = NewSession();

        Assert.False(session.WantsAudioCapture);
        Assert.False(session.IsSubscribedToAudio);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task UnsubscribeAudio_RevokesCaptureDemand()
    {
        var (session, socket) = await StartAuthenticatedAsync();
        await SubscribeAsync(socket, "\"media\",\"audio\"");
        await PlayStartAsync(socket);
        Assert.True(session.WantsAudioCapture);

        await UnsubscribeAsync(socket, "\"audio\"");

        Assert.False(session.WantsAudioCapture);
    }

    [Fact]
    public async Task ResubscribeWithoutAudio_RevokesCaptureDemand()
    {
        // subscribe 是整体替换语义：重发 ["media","lyrics"] 会把 audio 顶掉，
        // 需求必须随之消失，否则采集会在客户端已不再收音频时继续跑。
        var (session, socket) = await StartAuthenticatedAsync();
        await SubscribeAsync(socket, "\"audio\"");
        await PlayStartAsync(socket);
        Assert.True(session.WantsAudioCapture);

        await SubscribeAsync(socket, "\"media\",\"lyrics\"");

        Assert.False(session.WantsAudioCapture);
    }

    // ---- 重算信号 ----

    [Fact]
    public async Task PlayStart_RaisesDemandChanged()
    {
        var signals = 0;
        var (_, socket) = await StartAuthenticatedAsync(new MediaLinkSessionOptions
        {
            ExpectedToken = "t",
            OnAudioCaptureDemandChangedAsync = () => { Interlocked.Increment(ref signals); return Task.CompletedTask; }
        });

        await PlayStartAsync(socket);

        await WaitUntilAsync(() => Volatile.Read(ref signals) >= 1);
    }

    [Fact]
    public async Task RepeatedPlayStart_IsIdempotent()
    {
        // 台账第 2 条：play_start 无幂等保护，重复发重复触发回调。
        // 状态量天然幂等——不需要判重代码，重复请求后需求仍是同一个真值。
        // 协议上两次都必须回 ok：ok 是对「请求已收到」的应答，不是对「状态已改变」的应答。
        var (session, socket) = await StartAuthenticatedAsync();
        await SubscribeAsync(socket, "\"audio\"");

        await PlayStartAsync(socket, "p1");
        await PlayStartAsync(socket, "p2");

        Assert.True(session.WantsAudioCapture);
        Assert.Contains(socket.Outgoing, o => o.Contains("p2"));
        Assert.DoesNotContain(socket.Outgoing, o => o.Contains("error"));
    }

    [Fact]
    public async Task SubscribeAndUnsubscribe_RaiseDemandChanged()
    {
        var signals = 0;
        var (_, socket) = await StartAuthenticatedAsync(new MediaLinkSessionOptions
        {
            ExpectedToken = "t",
            OnAudioCaptureDemandChangedAsync = () => { Interlocked.Increment(ref signals); return Task.CompletedTask; }
        });

        await SubscribeAsync(socket, "\"audio\"");
        await WaitUntilAsync(() => Volatile.Read(ref signals) >= 1);
        var afterSubscribe = Volatile.Read(ref signals);

        await UnsubscribeAsync(socket, "\"audio\"");

        await WaitUntilAsync(() => Volatile.Read(ref signals) > afterSubscribe);
    }

    [Fact]
    public async Task SessionClosed_RevokesCaptureDemand()
    {
        // 台账第 1 条（本期核心）：最后一个订阅者掉线后采集永不停止，是真实资源泄漏。
        // 状态量把「断连」变成需求自然消失，不依赖任何补发的 stop。
        var hub = new MediaLinkSessionHub();
        var (session, socket) = await StartAuthenticatedAsync();
        hub.Add(session);
        await SubscribeAsync(socket, "\"audio\"");
        await PlayStartAsync(socket);
        Assert.True(hub.HasDownstreamAudioDemand);

        socket.EnqueueClose();

        await WaitUntilAsync(() => !hub.HasDownstreamAudioDemand);
    }

    [Fact]
    public async Task DemandSurvivesUntilLastSubscriberLeaves()
    {
        var hub = new MediaLinkSessionHub();
        var (first, firstSocket) = await StartAuthenticatedAsync();
        var (second, secondSocket) = await StartAuthenticatedAsync();
        hub.Add(first);
        hub.Add(second);

        await SubscribeAsync(firstSocket, "\"audio\"");
        await PlayStartAsync(firstSocket);
        await SubscribeAsync(secondSocket, "\"audio\"");
        await PlayStartAsync(secondSocket);
        Assert.True(hub.HasDownstreamAudioDemand);

        firstSocket.EnqueueClose();
        await WaitUntilAsync(() => first.IsClosed);
        Assert.True(hub.HasDownstreamAudioDemand);

        secondSocket.EnqueueClose();

        await WaitUntilAsync(() => !hub.HasDownstreamAudioDemand);
    }

    // ---- 协议表面回归锁定 ----

    [Fact]
    public async Task PlayStart_BeforeAuth_StillReturnsUnauthorized()
    {
        var (_, socket) = NewSession();

        socket.EnqueueIncoming("""{"type":"audio.play_start","id":"p1","v":1,"ts":0}""");

        await WaitUntilAsync(() => socket.Outgoing.Any(o => o.Contains("unauthorized")));
    }

    [Fact]
    public async Task PlayStart_WithoutAudioSubscription_StillReturnsOk()
    {
        // 协议已冻结：「两者只表达采集意愿，不改变订阅状态」（docs/medialink-protocol.md:458）。
        // 客户端先 play_start 再 subscribe 是合法顺序，回 bad_request 会把它判死。
        // 这条测试存在的意义是防后人「顺手」在应答处加门禁。
        var (_, socket) = await StartAuthenticatedAsync();
        await SubscribeAsync(socket, "\"media\"");

        socket.EnqueueIncoming("""{"type":"audio.play_start","id":"p9","v":1,"ts":0}""");

        await WaitUntilAsync(() => socket.Outgoing.Any(o => o.Contains("p9")));
        Assert.DoesNotContain(socket.Outgoing, o => o.Contains("bad_request"));
        Assert.Contains(socket.Outgoing, o => o.Contains("\"type\":\"ok\""));
    }

    // ---- IMP-2：音频写者失败必须终结会话 ----

    [Fact]
    public async Task AudioWriterSendFailure_ClosesSession()
    {
        // final-review IMP-2：音频写者发送失败后直接退出，不设 _closed 也不 Complete()，
        // 音频永久停摆且无自愈。JSON 写者本就有对称的 finally（MediaLinkSession.cs WriteLoopAsync）。
        // 两者共用同一个 socket 与 _sendLock——音频发不出去意味着 socket 已坏，JSON 也一样。
        var socket = new ThrowingBinarySocket();
        var session = new MediaLinkSession(socket, new MediaLinkSessionOptions { ExpectedToken = "t" });
        session.StartWriter(CancellationToken.None);
        _ = session.RunAsync(CancellationToken.None);

        socket.EnqueueIncoming("""{"type":"auth","id":"a1","v":1,"ts":0,"payload":{"token":"t"}}""");
        await WaitUntilAsync(() => session.IsAuthenticated);
        socket.EnqueueIncoming(
            """{"type":"subscribe","id":"s1","v":1,"ts":0,"payload":{"channels":["audio"]}}""");
        await WaitUntilAsync(() => session.IsSubscribedToAudio);

        await session.EnqueueAudioAsync(SampleFrame(), CancellationToken.None);

        await WaitUntilAsync(() => session.IsClosed);
    }

    // ---- Hub 音频广播 ----

    [Fact]
    public async Task BroadcastAudioFrame_OnlyReachesAudioSubscribers()
    {
        var hub = new MediaLinkSessionHub();
        var (listener, listenerSocket) = await StartAuthenticatedAsync();
        var (bystander, bystanderSocket) = await StartAuthenticatedAsync();
        hub.Add(listener);
        hub.Add(bystander);

        await SubscribeAsync(listenerSocket, "\"audio\"");
        await SubscribeAsync(bystanderSocket, "\"media\",\"lyrics\"");

        await hub.BroadcastAudioFrameAsync(SampleFrame("tk-broadcast"), CancellationToken.None);

        await WaitUntilAsync(() => listenerSocket.OutgoingBinary.Count == 1);
        Assert.True(MediaLinkAudioFrame.TryDecode(
            listenerSocket.OutgoingBinary.First(), out var header, out _, out _));
        Assert.Equal("tk-broadcast", header.TrackToken);
        Assert.Empty(bystanderSocket.OutgoingBinary);
    }

    [Fact]
    public async Task BroadcastAudioFrame_ClosedSessionDoesNotStarveOthers()
    {
        // 一个会话已死不应让广播中途夭折，后面的会话必须照收——与 BroadcastMediaUpdatedAsync 同构。
        var hub = new MediaLinkSessionHub();
        var (dead, deadSocket) = await StartAuthenticatedAsync();
        var (alive, aliveSocket) = await StartAuthenticatedAsync();
        await SubscribeAsync(deadSocket, "\"audio\"");
        await SubscribeAsync(aliveSocket, "\"audio\"");
        hub.Add(dead);
        hub.Add(alive);

        deadSocket.EnqueueClose();
        await WaitUntilAsync(() => dead.IsClosed);

        await hub.BroadcastAudioFrameAsync(SampleFrame("after-death"), CancellationToken.None);

        await WaitUntilAsync(() => aliveSocket.OutgoingBinary.Count == 1);
        Assert.Empty(deadSocket.OutgoingBinary);
    }

    /// <summary>二进制发送必失败的 socket，用于逼出音频写者的失败路径。</summary>
    private sealed class ThrowingBinarySocket : IMediaLinkSocket
    {
        private readonly FakeMediaLinkSocket _inner = new();

        public WebSocketState State => _inner.State;

        public void EnqueueIncoming(string text) => _inner.EnqueueIncoming(text);

        public Task SendTextAsync(string text, CancellationToken cancellationToken) =>
            _inner.SendTextAsync(text, cancellationToken);

        public Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
            throw new IOException("对端已断开");

        public Task<MediaLinkSocketMessage> ReceiveAsync(CancellationToken cancellationToken) =>
            _inner.ReceiveAsync(cancellationToken);

        public Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken cancellationToken) =>
            _inner.CloseAsync(status, description, cancellationToken);
    }
}
