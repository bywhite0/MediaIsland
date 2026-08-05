using System.Net.WebSockets;
using MediaIsland.Services.MediaLink;
using Xunit;

namespace MediaIsland.Tests.MediaLink;

/// <summary>
/// 测试服务停止时的 1001 GoingAway 发送逻辑，确保客户端能区分正常下线与网络故障。
/// </summary>
public class MediaLinkGoingAwayTests
{
    [Fact]
    public async Task CloseGoingAwayAsync_UsesCloseOutputAsync_NotCloseAsync()
    {
        // CloseOutputAsync 只发不等，避免停服时等待对端握手而超时来不及发帧。
        var socket = new CloseMethodTrackingSocket();
        var session = new MediaLinkSession(
            socket,
            new MediaLinkSessionOptions { ExpectedToken = "tok" });

        await session.CloseGoingAwayAsync();

        Assert.True(socket.CloseOutputCalled, "应使用 CloseOutputAsync（只发不等）");
        Assert.False(socket.CloseAsyncCalled, "不应使用 CloseAsync（要等握手）");
        Assert.Equal((WebSocketCloseStatus)1001, socket.LastCloseStatus);
        Assert.Equal("server stopping", socket.LastCloseDescription);
    }

    [Fact]
    public async Task CloseGoingAwayAsync_IsIdempotent()
    {
        // AppStopping 与 IHostedService.StopAsync 可能相继触发，第二次调用应静默返回。
        var socket = new CloseMethodTrackingSocket();
        var session = new MediaLinkSession(
            socket,
            new MediaLinkSessionOptions { ExpectedToken = "tok" });

        await session.CloseGoingAwayAsync();
        var firstCallCount = socket.CloseOutputCallCount;

        await session.CloseGoingAwayAsync();
        await session.CloseGoingAwayAsync();

        Assert.Equal(firstCallCount, socket.CloseOutputCallCount);
    }

    [Fact]
    public async Task CloseAllGoingAwayAsync_IsConcurrent_NotSerial()
    {
        // 32 个会话，串行则累加 32 秒，远超宿主窗口；并发后总耗时收敛为单会话超时。
        var hub = new MediaLinkSessionHub();
        var sessions = Enumerable.Range(0, 32)
            .Select(_ =>
            {
                var socket = new DelayedCloseSocket(TimeSpan.FromMilliseconds(100));
                var session = new MediaLinkSession(
                    socket,
                    new MediaLinkSessionOptions { ExpectedToken = "tok" });
                hub.Add(session);
                return session;
            })
            .ToList();

        var start = DateTimeOffset.UtcNow;
        await hub.CloseAllGoingAwayAsync(TimeSpan.FromSeconds(1));
        var elapsed = DateTimeOffset.UtcNow - start;

        // 并发情况下总耗时应接近单会话延迟（100ms），远小于串行的 3.2s。
        Assert.True(elapsed < TimeSpan.FromSeconds(1), $"总耗时 {elapsed.TotalMilliseconds}ms 应远小于串行的 3200ms");
    }

    /// <summary>记录调用了哪个关闭方法的 fake socket。</summary>
    private sealed class CloseMethodTrackingSocket : IMediaLinkSocket
    {
        public WebSocketState State { get; private set; } = WebSocketState.Open;
        public bool CloseAsyncCalled { get; private set; }
        public bool CloseOutputCalled { get; private set; }
        public int CloseOutputCallCount { get; private set; }
        public WebSocketCloseStatus? LastCloseStatus { get; private set; }
        public string? LastCloseDescription { get; private set; }

        public Task SendTextAsync(string text, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<string?> ReceiveTextAsync(CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);

        public Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken cancellationToken)
        {
            CloseAsyncCalled = true;
            State = WebSocketState.Closed;
            LastCloseStatus = status;
            LastCloseDescription = description;
            return Task.CompletedTask;
        }

        public Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken cancellationToken)
        {
            CloseOutputCalled = true;
            CloseOutputCallCount++;
            State = WebSocketState.CloseSent;
            LastCloseStatus = status;
            LastCloseDescription = description;
            return Task.CompletedTask;
        }
    }

    /// <summary>模拟关闭握手慢的 socket，用于验证并发行为。</summary>
    private sealed class DelayedCloseSocket(TimeSpan delay) : IMediaLinkSocket
    {
        public WebSocketState State { get; private set; } = WebSocketState.Open;

        public Task SendTextAsync(string text, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<string?> ReceiveTextAsync(CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);

        public Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken cancellationToken) =>
            Task.Delay(delay, cancellationToken);

        public async Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            State = WebSocketState.CloseSent;
        }
    }
}
