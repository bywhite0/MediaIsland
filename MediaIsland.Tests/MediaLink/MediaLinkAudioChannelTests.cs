using System.Net.WebSockets;
using MediaIsland.Services.MediaLink;
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
