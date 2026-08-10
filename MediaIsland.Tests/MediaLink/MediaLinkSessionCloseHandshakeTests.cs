using System.Net.WebSockets;
using MediaIsland.Services.MediaLink;
using Xunit;

namespace MediaIsland.Tests.MediaLink;

/// <summary>
/// 客户端发起关闭时服务端要回一次单向关闭。
///
/// 不回应的话连接只能等超时结束，而本期的音源仲裁会让上游连接随媒体来源变化
/// 频繁开关，每次未完成的握手都留下一个等超时的连接。
///
/// 回的是 CloseOutputAsync 而不是 CloseAsync：对端已经发过 Close，
/// 此时是单向回应，用 CloseAsync 等于再发起一次双向握手并等对方回，
/// 而对方已经在等我们了。
/// </summary>
public class MediaLinkSessionCloseHandshakeTests
{
    [Fact]
    public async Task ClientInitiatedClose_IsAnsweredWithCloseOutput()
    {
        var socket = new ClosingSocket();
        var session = new MediaLinkSession(
            socket,
            new MediaLinkSessionOptions { ExpectedToken = "tok" });

        await session.RunAsync(CancellationToken.None);

        Assert.True(socket.CloseOutputCalled, "应回一次 CloseOutputAsync");
        Assert.False(socket.CloseAsyncCalled, "不应用 CloseAsync 再发起一次双向握手");
        Assert.Equal(WebSocketCloseStatus.NormalClosure, socket.LastCloseStatus);
    }

    [Fact]
    public async Task ClientInitiatedClose_StillEndsTheReceiveLoop()
    {
        // 地基：回应握手不能把收循环变成不退出。ReceiveAsync 恒返回关闭消息，
        // 不 break 的话这条测试会挂死而不是失败。
        var socket = new ClosingSocket();
        var session = new MediaLinkSession(
            socket,
            new MediaLinkSessionOptions { ExpectedToken = "tok" });

        var run = session.RunAsync(CancellationToken.None);
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(run, finished);
    }

    [Fact]
    public async Task FailingCloseHandshake_DoesNotPropagate()
    {
        // 对端可能已经走了。握手回不去不是错误，收循环照常退出。
        var socket = new ClosingSocket { ThrowOnCloseOutput = true };
        var session = new MediaLinkSession(
            socket,
            new MediaLinkSessionOptions { ExpectedToken = "tok" });

        await session.RunAsync(CancellationToken.None);
    }

    /// <summary>收到即报「对端已关闭」的 socket，并记录服务端回了哪个关闭方法。</summary>
    private sealed class ClosingSocket : IMediaLinkSocket
    {
        public WebSocketState State { get; private set; } = WebSocketState.Open;
        public bool CloseAsyncCalled { get; private set; }
        public bool CloseOutputCalled { get; private set; }
        public WebSocketCloseStatus? LastCloseStatus { get; private set; }
        public bool ThrowOnCloseOutput { get; set; }

        public Task SendTextAsync(string text, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        // default 即 Text 与 Binary 皆为 null，也就是 IsClosed。
        public Task<MediaLinkSocketMessage> ReceiveAsync(CancellationToken cancellationToken) =>
            Task.FromResult(default(MediaLinkSocketMessage));

        public Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken cancellationToken)
        {
            CloseAsyncCalled = true;
            State = WebSocketState.Closed;
            LastCloseStatus = status;
            return Task.CompletedTask;
        }

        public Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken cancellationToken)
        {
            CloseOutputCalled = true;
            State = WebSocketState.CloseSent;
            LastCloseStatus = status;
            return ThrowOnCloseOutput
                ? Task.FromException(new WebSocketException("对端已经走了"))
                : Task.CompletedTask;
        }
    }
}
