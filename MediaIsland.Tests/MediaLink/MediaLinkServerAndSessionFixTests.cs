using System.Net.WebSockets;
using System.Text;
using MediaIsland.Services.MediaLink;
using MediaIsland.Services.MediaLink.Protocol;
using Xunit;

namespace MediaIsland.Tests.MediaLink;

public class MediaLinkServerUpgradeTests
{
    [Fact]
    public async Task TryUpgradeAsync_Writes101AndDoesNotConsumePastHeaders()
    {
        var request =
            "GET /v1/ws HTTP/1.1\r\n" +
            "Host: 127.0.0.1\r\n" +
            "Connection: Upgrade\r\n" +
            "Upgrade: websocket\r\n" +
            "Sec-WebSocket-Version: 13\r\n" +
            "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n" +
            "\r\n";
        var trailing = new byte[] { 0x81, 0x05, (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o' };

        // 可读写包装：读请求 + 写 101 响应到同一 MemoryStream 会交错，用双缓冲流
        var duplex = new DuplexMemoryStream(
            Encoding.ASCII.GetBytes(request).Concat(trailing).ToArray());

        var upgraded = await MediaLinkServer.TryUpgradeAsync(duplex, "127.0.0.1", null, CancellationToken.None);
        Assert.True(upgraded);

        var leftover = duplex.ReadRemainingInput();
        Assert.Equal(trailing, leftover);

        var responseText = Encoding.ASCII.GetString(duplex.GetWrittenBytes());
        Assert.Contains("101 Switching Protocols", responseText, StringComparison.Ordinal);
        Assert.Contains("Sec-WebSocket-Accept:", responseText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadHttpHeadersAsync_DoesNotReadPastTerminator()
    {
        var header = "GET /v1/ws HTTP/1.1\r\nHost: x\r\n\r\n";
        var after = Encoding.ASCII.GetBytes("AFTER");
        var input = Encoding.ASCII.GetBytes(header).Concat(after).ToArray();
        using var stream = new MemoryStream(input);

        var text = await MediaLinkServer.ReadHttpHeadersAsync(stream, CancellationToken.None);
        Assert.EndsWith("\r\n\r\n", text, StringComparison.Ordinal);

        var remaining = new byte[after.Length];
        Assert.Equal(after.Length, await stream.ReadAsync(remaining));
        Assert.Equal(after, remaining);
    }

    [Fact]
    public void ValidateUpgradeRequest_WrongPath_Returns404()
    {
        var result = MediaLinkServer.ValidateUpgradeRequest(
            "/v1/wx", "GET", MakeHeaders(), "127.0.0.1", null, out _);
        Assert.NotNull(result);
        Assert.Contains("404", result);
    }

    [Fact]
    public void ValidateUpgradeRequest_PathWithQuery_ReturnsNull()
    {
        var result = MediaLinkServer.ValidateUpgradeRequest(
            "/v1/ws?foo=1", "GET", MakeHeaders(), "127.0.0.1", null, out _);
        Assert.Null(result);
    }

    [Fact]
    public void ValidateUpgradeRequest_MissingUpgrade_Returns400()
    {
        var headers = MakeHeaders();
        headers.Remove("Upgrade");
        var result = MediaLinkServer.ValidateUpgradeRequest(
            "/v1/ws", "GET", headers, "127.0.0.1", null, out _);
        Assert.NotNull(result);
        Assert.Contains("400", result);
    }

    [Fact]
    public void ValidateUpgradeRequest_WrongVersion_Returns426()
    {
        var headers = MakeHeaders();
        headers["Sec-WebSocket-Version"] = "8";
        var result = MediaLinkServer.ValidateUpgradeRequest(
            "/v1/ws", "GET", headers, "127.0.0.1", null, out _);
        Assert.NotNull(result);
        Assert.Contains("426", result);
        Assert.Contains("Sec-WebSocket-Version: 13", result);
    }

    [Fact]
    public void ValidateUpgradeRequest_BadKey_Returns400()
    {
        var headers = MakeHeaders();
        headers["Sec-WebSocket-Key"] = "not-base64!!!";
        var result = MediaLinkServer.ValidateUpgradeRequest(
            "/v1/ws", "GET", headers, "127.0.0.1", null, out _);
        Assert.NotNull(result);
        Assert.Contains("400", result);
    }

    [Fact]
    public void ValidateUpgradeRequest_HostEvil_Returns403()
    {
        var headers = MakeHeaders();
        headers["Host"] = "evil.com";
        var result = MediaLinkServer.ValidateUpgradeRequest(
            "/v1/ws", "GET", headers, "127.0.0.1", null, out _);
        Assert.NotNull(result);
        Assert.Contains("403", result);
    }

    [Fact]
    public void ValidateUpgradeRequest_OriginNotAllowed_Returns403()
    {
        var headers = MakeHeaders();
        headers["Origin"] = "https://evil.com";
        var result = MediaLinkServer.ValidateUpgradeRequest(
            "/v1/ws", "GET", headers, "127.0.0.1", new HashSet<string>(), out _);
        Assert.NotNull(result);
        Assert.Contains("403", result);
    }

    [Fact]
    public void ValidateUpgradeRequest_OriginNullWithNullAllowed_ReturnsNull()
    {
        var headers = MakeHeaders();
        headers["Origin"] = "null";
        var result = MediaLinkServer.ValidateUpgradeRequest(
            "/v1/ws", "GET", headers, "127.0.0.1", new HashSet<string> { "null" }, out _);
        Assert.Null(result);
    }

    [Fact]
    public void ValidateUpgradeRequest_NonGet_Returns400()
    {
        var result = MediaLinkServer.ValidateUpgradeRequest(
            "/v1/ws", "POST", MakeHeaders(), "127.0.0.1", null, out _);
        Assert.NotNull(result);
        Assert.Contains("400", result);
    }

    [Fact]
    public void AuthFailureRateLimit_BlocksAfter5Failures()
    {
        var hub = new MediaLinkSessionHub();
        var server = new MediaLinkServer(hub, _ => Task.CompletedTask, () => "token");

        for (var i = 0; i < MediaLinkServer.AuthFailureLimit; i++)
        {
            Assert.False(server.IsAuthRateLimited("192.168.1.1"));
            server.RecordAuthFailure("192.168.1.1");
        }

        Assert.True(server.IsAuthRateLimited("192.168.1.1"));
        Assert.False(server.IsAuthRateLimited("192.168.1.2"));
    }
    private static Dictionary<string, string> MakeHeaders() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Host"] = "127.0.0.1",
        ["Connection"] = "Upgrade",
        ["Upgrade"] = "websocket",
        ["Sec-WebSocket-Version"] = "13",
        ["Sec-WebSocket-Key"] = "dGhlIHNhbXBsZSBub25jZQ=="
    };
    private sealed class DuplexMemoryStream : Stream
    {
        private readonly MemoryStream _input;
        private readonly MemoryStream _output = new();

        public DuplexMemoryStream(byte[] input)
        {
            _input = new MemoryStream(input);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) =>
            _input.Read(buffer, offset, count);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _input.ReadAsync(buffer, offset, count, cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _input.ReadAsync(buffer, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) =>
            _output.Write(buffer, offset, count);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            _output.Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _output.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public byte[] GetWrittenBytes() => _output.ToArray();

        public byte[] ReadRemainingInput()
        {
            var left = new byte[_input.Length - _input.Position];
            _ = _input.Read(left);
            return left;
        }
    }
}

public class MediaLinkSessionConcurrencyAndAuthTests
{
    [Fact]
    public async Task ConcurrentSendAsync_SerializesWithoutOverlap()
    {
        var socket = new OverlapTrackingSocket();
        var session = new MediaLinkSession(socket, new MediaLinkSessionOptions { ExpectedToken = "t" });

        // 认证后才能 ping；直接并发 SendAsync
        var tasks = Enumerable.Range(0, 32)
            .Select(i => session.SendAsync(
                MediaLinkMessageSerializer.Create(MediaLinkProtocol.TypeEvent, name: $"n{i}"),
                CancellationToken.None))
            .ToArray();

        await Task.WhenAll(tasks);
        // 串行发送：任意时刻 in-flight 最多 1
        Assert.Equal(1, socket.MaxOverlap);
        Assert.Equal(32, socket.SendCount);
    }

    [Fact]
    public async Task PreAuthPing_SendsUnauthorizedAndCloses()
    {
        var socket = new FakeMediaLinkSocket();
        var session = new MediaLinkSession(socket, new MediaLinkSessionOptions { ExpectedToken = "secret" });

        await session.HandleMessageAsync(
            MediaLinkMessageSerializer.Serialize(MediaLinkMessageSerializer.Create(
                MediaLinkProtocol.TypePing,
                id: "p1")),
            CancellationToken.None);

        Assert.Contains(socket.Outgoing, json =>
            json.Contains(MediaLinkProtocol.TypeError, StringComparison.Ordinal) &&
            json.Contains(MediaLinkProtocol.ErrorUnauthorized, StringComparison.Ordinal));
        Assert.Equal(WebSocketState.Closed, socket.State);
    }

    [Fact]
    public async Task PreAuthPhase2Command_SendsUnauthorizedAndCloses()
    {
        var socket = new FakeMediaLinkSocket();
        var session = new MediaLinkSession(socket, new MediaLinkSessionOptions { ExpectedToken = "secret" });

        await session.HandleMessageAsync(
            MediaLinkMessageSerializer.Serialize(MediaLinkMessageSerializer.Create(
                "playback.command",
                id: "c1")),
            CancellationToken.None);

        Assert.Contains(socket.Outgoing, json =>
            json.Contains(MediaLinkProtocol.ErrorUnauthorized, StringComparison.Ordinal));
        Assert.Equal(WebSocketState.Closed, socket.State);
    }

    private sealed class OverlapTrackingSocket : IMediaLinkSocket
    {
        private int _inFlight;
        private int _maxOverlap;
        private int _sendCount;

        public int MaxOverlap => Volatile.Read(ref _maxOverlap);
        public int SendCount => Volatile.Read(ref _sendCount);

        public WebSocketState State => WebSocketState.Open;

        public async Task SendTextAsync(string text, CancellationToken cancellationToken)
        {
            var now = Interlocked.Increment(ref _inFlight);
            Interlocked.Increment(ref _sendCount);
            while (true)
            {
                var observed = Volatile.Read(ref _maxOverlap);
                if (now <= observed || Interlocked.CompareExchange(ref _maxOverlap, now, observed) == observed)
                {
                    break;
                }
            }

            await Task.Delay(5, cancellationToken);
            Interlocked.Decrement(ref _inFlight);
        }

        public Task<string?> ReceiveTextAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
