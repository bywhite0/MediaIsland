using System.Net.WebSockets;
using System.Text;
using MediaIsland.Services.Audio;
using MediaIsland.Services.MediaLink;
using Xunit;

namespace MediaIsland.Tests.MediaLink;

/// <summary>
/// 「发出时刻」字段必须量在拿到写入权之后，而不是排队开始时。
///
/// 这条性质此前被放在了错误的一层：会话级的 _sendLock 几乎从不阻塞，而真正每约
/// 10 毫秒争用一次的是 socket 级那把（SendTextAsync 与 SendBinaryAsync 同锁）。
/// 挡错层的代码照样能发出报文，顺序断言也照样绿——只有把「构造发生在等待之后」
/// 直接做成判据，才拦得住那种改动。
/// </summary>
public class MediaLinkSocketLateStampTests
{
    private static async Task WithinAsync(Task task, string what, int timeoutMs = 5_000)
    {
        // 竞争形式而非直接 await：被测行为若退化成永不完成，判据必须变红而不是挂住。
        var finished = await Task.WhenAny(task, Task.Delay(timeoutMs));
        Assert.True(ReferenceEquals(finished, task), $"{what} 在 {timeoutMs}ms 内未完成");
        await task;
    }

    [Fact]
    public async Task TextFactory_RunsAfterTheContendedWriteLockIsAcquired()
    {
        var webSocket = new BlockingWebSocket();
        using var socket = new WebSocketMediaLinkSocket(webSocket);

        // 先让一个二进制写占住 socket 级写入权——这正是音频推流时的常态。
        var binary = socket.SendBinaryAsync(new byte[] { 1, 2, 3 }, CancellationToken.None);
        await WithinAsync(webSocket.EnteredSend.Task, "二进制写进入 socket");

        long stampedAt = 0;
        var text = socket.SendTextAsync(
            () =>
            {
                stampedAt = MonotonicClock.Now100Ns();
                return "{\"t3\":0}";
            },
            CancellationToken.None);

        // 文本写此刻应当仍在排队。断言这一点是必要的地基：若它已经完成，
        // 后面那条时刻比较就只是在比两个几乎同时的数，恒真而无意义。
        await Task.Delay(50);
        Assert.False(text.IsCompleted, "文本写没有在等写入权，说明两条路径并未共用同一把锁");
        Assert.Equal(0, stampedAt);

        var releasedAt = MonotonicClock.Now100Ns();
        webSocket.ReleaseSend.SetResult();

        await WithinAsync(binary, "二进制写");
        await WithinAsync(text, "文本写");

        Assert.True(
            stampedAt >= releasedAt,
            $"时刻 {stampedAt} 早于写入权释放时刻 {releasedAt}，说明报文在排队之前就被构造了");
    }

    [Fact]
    public async Task StringOverload_StillSendsWhatItWasGiven()
    {
        // 新增的工厂重载不得改变既有那条路径的行为。
        var webSocket = new BlockingWebSocket();
        webSocket.ReleaseSend.SetResult();
        using var socket = new WebSocketMediaLinkSocket(webSocket);

        await WithinAsync(socket.SendTextAsync("hello", CancellationToken.None), "文本写");

        Assert.Equal("hello", Encoding.UTF8.GetString(webSocket.LastSent!));
        Assert.Equal(WebSocketMessageType.Text, webSocket.LastSentType);
    }

    /// <summary>
    /// 一个把发送卡住的 WebSocket：进入 SendAsync 时鸣笛，然后等外部放行。
    /// </summary>
    private sealed class BlockingWebSocket : WebSocket
    {
        internal TaskCompletionSource EnteredSend { get; } = new();

        internal TaskCompletionSource ReleaseSend { get; } = new();

        internal byte[]? LastSent { get; private set; }

        internal WebSocketMessageType LastSentType { get; private set; }

        public override WebSocketCloseStatus? CloseStatus => null;

        public override string? CloseStatusDescription => null;

        public override WebSocketState State => WebSocketState.Open;

        public override string? SubProtocol => null;

        public override void Abort()
        {
        }

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override void Dispose()
        {
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
            Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));

        public override async Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            LastSent = buffer.ToArray();
            LastSentType = messageType;
            EnteredSend.TrySetResult();
            await ReleaseSend.Task;
        }
    }
}
