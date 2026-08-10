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
        // 地基：回应握手不能把收循环变成不退出。替身回握手后仍报 Open，于是「退出」
        // 只可能来自 break 本身，而不是循环条件恰好转假——后者会让这条测试即便在
        // break 被删掉时也照样通过。
        var socket = new ClosingSocket { StaysOpenAfterClose = true };
        var session = new MediaLinkSession(
            socket,
            new MediaLinkSessionOptions { ExpectedToken = "tok" });

        await session.RunAsync(CancellationToken.None);

        Assert.Equal(1, socket.ReceiveCount);
    }

    [Fact]
    public async Task FailingCloseHandshake_DoesNotPropagate()
    {
        // 对端可能已经走了，也可能是写超时。握手回不去不是错误，收循环照常退出。
        //
        // 抛的是 OperationCanceledException 而不是随便一个异常：RunAsync 最外层的
        // catch 过滤器明确排除 OCE，所以只有 Close 分支自带的 catch 接得住它。
        // 换成别的异常类型，这条测试验的就是最外层的兜底，跟本分支无关。
        var socket = new ClosingSocket { ThrowOnCloseOutput = true };
        var session = new MediaLinkSession(
            socket,
            new MediaLinkSessionOptions { ExpectedToken = "tok" });

        var error = await Record.ExceptionAsync(() => session.RunAsync(CancellationToken.None));

        Assert.Null(error);
    }

    [Fact]
    public async Task SocketAlreadyClosedByReceiver_IsNotAnswered()
    {
        // IsClosed 还有第二个来源：消息超限时接收侧会自己先关掉 socket 再报 IsClosed。
        // 那条路径走到 Close 分支时 socket 已经是 Closed，此时回握手必抛，
        // 而未认证的对端可以反复触发它。
        var socket = new SelfClosingOnReceiveSocket();
        var session = new MediaLinkSession(
            socket,
            new MediaLinkSessionOptions { ExpectedToken = "tok" });

        await session.RunAsync(CancellationToken.None);

        Assert.False(socket.CloseOutputCalled, "socket 已经关了就不该再回握手");
    }

    [Fact]
    public async Task CloseHandshakeWrite_IsBounded()
    {
        // 对端不读数据时，关闭帧的写会一直挂着。这次 await 挡在 RunAsync 的 finally
        // 之前，而 finally 里要撤销本会话的音频采集需求——写无界，采集设备的释放就跟着
        // 无限期推迟。
        //
        // 外层故意传 CancellationToken.None：能让 RunAsync 回来的就只剩写自带的超时，
        // 判据因此不会被外部取消冒名顶替。
        var socket = new HangingCloseSocket();
        var session = new MediaLinkSession(
            socket,
            new MediaLinkSessionOptions { ExpectedToken = "tok" });

        var run = session.RunAsync(CancellationToken.None);
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(30)));

        Assert.True(socket.CloseTokenCanBeCanceled, "关闭帧的写应绑在一个可取消的 token 上");
        Assert.Same(run, finished);
        await run;
    }

    /// <summary>收到即报「对端已关闭」的 socket，并记录服务端回了哪个关闭方法。</summary>
    private sealed class ClosingSocket : IMediaLinkSocket
    {
        public WebSocketState State { get; private set; } = WebSocketState.Open;
        public bool CloseAsyncCalled { get; private set; }
        public bool CloseOutputCalled { get; private set; }
        public WebSocketCloseStatus? LastCloseStatus { get; private set; }
        public int ReceiveCount { get; private set; }

        /// <summary>回握手后不改 State，用于把「靠 break 退出」与「靠状态转假退出」分开。</summary>
        public bool StaysOpenAfterClose { get; set; }

        /// <summary>回握手时抛 OperationCanceledException，模拟写超时打断。</summary>
        public bool ThrowOnCloseOutput { get; set; }

        public Task SendTextAsync(string text, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        // default 即 Text 与 Binary 皆为 null，也就是 IsClosed。
        public Task<MediaLinkSocketMessage> ReceiveAsync(CancellationToken cancellationToken)
        {
            ReceiveCount++;
            // 收到 Close 后还来第二次就是没退出。到点抛出而不是继续喂，是因为本替身所有
            // await 都同步完成：收循环不退出的话 RunAsync 根本不把控制权交还，测试进程
            // 会直接挂死，连超时脚手架都轮不到执行。
            return ReceiveCount > 3
                ? Task.FromException<MediaLinkSocketMessage>(new InvalidOperationException("收循环未退出"))
                : Task.FromResult(default(MediaLinkSocketMessage));
        }

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
            if (!StaysOpenAfterClose)
            {
                State = WebSocketState.CloseSent;
            }

            LastCloseStatus = status;
            return ThrowOnCloseOutput
                ? Task.FromException(new OperationCanceledException("写超时"))
                : Task.CompletedTask;
        }
    }

    /// <summary>
    /// 接收侧自行关闭后再报 IsClosed，与消息超限那条路径同序（先关，后报）。
    /// </summary>
    private sealed class SelfClosingOnReceiveSocket : IMediaLinkSocket
    {
        public WebSocketState State { get; private set; } = WebSocketState.Open;
        public bool CloseOutputCalled { get; private set; }

        public Task SendTextAsync(string text, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<MediaLinkSocketMessage> ReceiveAsync(CancellationToken cancellationToken)
        {
            State = WebSocketState.Closed;
            return Task.FromResult(default(MediaLinkSocketMessage));
        }

        public Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken cancellationToken)
        {
            State = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken cancellationToken)
        {
            CloseOutputCalled = true;
            return Task.CompletedTask;
        }
    }

    /// <summary>关闭帧的写永不自行完成，只有 token 被取消才结束——对端不读数据即如此。</summary>
    private sealed class HangingCloseSocket : IMediaLinkSocket
    {
        public WebSocketState State => WebSocketState.Open;
        public bool CloseTokenCanBeCanceled { get; private set; }

        public Task SendTextAsync(string text, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<MediaLinkSocketMessage> ReceiveAsync(CancellationToken cancellationToken) =>
            Task.FromResult(default(MediaLinkSocketMessage));

        public Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken cancellationToken)
        {
            CloseTokenCanBeCanceled = cancellationToken.CanBeCanceled;
            return Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }
}
