using System.Net.WebSockets;
using System.Text;
using MediaIsland.Services.MediaLink;
using MediaIsland.Services.MediaLink.Protocol;
using Xunit;

namespace MediaIsland.Tests.MediaLink;

/// <summary>
/// 传输层的消息组装上限。二进制与文本分开设限：二进制的字节在 socket 层就已全部落进
/// 托管堆，而认证判定要到会话层才发生，所以未认证连接能让服务端替它缓冲满额。
/// 这些测试锁定「二进制更严、文本不受影响」这两半，缺了任何一半上限都会静默失效。
/// </summary>
public class MediaLinkSocketMessageLimitTests
{
    [Fact]
    public async Task ReceiveAsync_RejectsBinaryOverBinaryLimit()
    {
        // 超限一个字节即拒。判定按分片累加，故这条同时覆盖「跨分片才超限」的情形。
        var webSocket = new ScriptedWebSocket();
        webSocket.Enqueue(WebSocketMessageType.Binary, new byte[WebSocketMediaLinkSocket.MaxBinaryMessageBytes + 1]);
        using var socket = new WebSocketMediaLinkSocket(webSocket);

        var message = await socket.ReceiveAsync(CancellationToken.None);

        Assert.True(message.IsClosed);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, webSocket.SentCloseStatus);
    }

    [Fact]
    public async Task ReceiveAsync_AcceptsBinaryAtBinaryLimit()
    {
        // 边界值必须收下，否则上限会从「拒绝超额」滑成「拒绝恰好满额」。
        var payload = new byte[WebSocketMediaLinkSocket.MaxBinaryMessageBytes];
        Random.Shared.NextBytes(payload);
        var webSocket = new ScriptedWebSocket();
        webSocket.Enqueue(WebSocketMessageType.Binary, payload);
        using var socket = new WebSocketMediaLinkSocket(webSocket);

        var message = await socket.ReceiveAsync(CancellationToken.None);

        Assert.Null(webSocket.SentCloseStatus);
        Assert.Equal(payload, message.Binary);
    }

    [Fact]
    public async Task ReceiveAsync_AcceptsRealisticAudioFrame()
    {
        // 上限存在的前提是它对合法流量毫无影响：20ms / 48kHz / 双声道 / i16 = 3840 字节 PCM。
        var pcm = new byte[3840];
        Random.Shared.NextBytes(pcm);
        var frame = MediaLinkAudioFrame.Encode(
            new MediaLinkAudioFrameHeader(0, 0, 0, 1, "limit-track", MediaLinkAudioFrameFlags.None),
            pcm);
        Assert.True(frame.Length < WebSocketMediaLinkSocket.MaxBinaryMessageBytes);

        var webSocket = new ScriptedWebSocket();
        webSocket.Enqueue(WebSocketMessageType.Binary, frame);
        using var socket = new WebSocketMediaLinkSocket(webSocket);

        var message = await socket.ReceiveAsync(CancellationToken.None);

        Assert.Null(webSocket.SentCloseStatus);
        Assert.Equal(frame, message.Binary);
    }

    [Fact]
    public async Task ReceiveAsync_AcceptsTextAboveBinaryLimit()
    {
        // 防回归的关键一条：文本的额度仍是 MaxMessageBytes，不能被二进制的新阈值误伤。
        var text = new string('x', WebSocketMediaLinkSocket.MaxBinaryMessageBytes + 4096);
        Assert.True(Encoding.UTF8.GetByteCount(text) > WebSocketMediaLinkSocket.MaxBinaryMessageBytes);
        Assert.True(Encoding.UTF8.GetByteCount(text) < WebSocketMediaLinkSocket.MaxMessageBytes);

        var webSocket = new ScriptedWebSocket();
        webSocket.Enqueue(WebSocketMessageType.Text, Encoding.UTF8.GetBytes(text));
        using var socket = new WebSocketMediaLinkSocket(webSocket);

        var message = await socket.ReceiveAsync(CancellationToken.None);

        Assert.Null(webSocket.SentCloseStatus);
        Assert.Equal(text, message.Text);
    }

    [Fact]
    public async Task ReceiveAsync_StillRejectsTextOverTextLimit()
    {
        // 另一半：文本上限本身没被拆掉。
        var webSocket = new ScriptedWebSocket();
        webSocket.Enqueue(WebSocketMessageType.Text, new byte[WebSocketMediaLinkSocket.MaxMessageBytes + 1]);
        using var socket = new WebSocketMediaLinkSocket(webSocket);

        var message = await socket.ReceiveAsync(CancellationToken.None);

        Assert.True(message.IsClosed);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, webSocket.SentCloseStatus);
    }

    /// <summary>
    /// 按调用方给的缓冲大小切片投递脚本化消息，以此复现真实 socket 的分片组装。
    /// 直接驱动 <see cref="WebSocketMediaLinkSocket"/> 而不走回环，是因为上限判定
    /// 只关心「累加了多少字节」，回环反而会把它埋在端口与认证时序里。
    /// </summary>
    private sealed class ScriptedWebSocket : WebSocket
    {
        private readonly Queue<(WebSocketMessageType Type, byte[] Payload)> _incoming = new();
        private (WebSocketMessageType Type, byte[] Payload)? _current;
        private int _offset;

        public WebSocketCloseStatus? SentCloseStatus { get; private set; }

        public void Enqueue(WebSocketMessageType type, byte[] payload) => _incoming.Enqueue((type, payload));

        public override WebSocketCloseStatus? CloseStatus => SentCloseStatus;

        public override string? CloseStatusDescription => null;

        public override WebSocketState State => SentCloseStatus is null ? WebSocketState.Open : WebSocketState.Closed;

        public override string? SubProtocol => null;

        public override void Abort() { }

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            SentCloseStatus = closeStatus;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            CloseAsync(closeStatus, statusDescription, cancellationToken);

        public override void Dispose() { }

        public override Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            if (_current is null)
            {
                if (_incoming.Count == 0)
                {
                    return Task.FromResult(
                        new WebSocketReceiveResult(0, WebSocketMessageType.Close, endOfMessage: true));
                }

                _current = _incoming.Dequeue();
                _offset = 0;
            }

            var (type, payload) = _current.Value;
            var count = Math.Min(buffer.Count, payload.Length - _offset);
            Array.Copy(payload, _offset, buffer.Array!, buffer.Offset, count);
            _offset += count;

            var endOfMessage = _offset >= payload.Length;
            if (endOfMessage)
            {
                _current = null;
            }

            return Task.FromResult(new WebSocketReceiveResult(count, type, endOfMessage));
        }

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
