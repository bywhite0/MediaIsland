using System.Net.WebSockets;
using System.Collections.Concurrent;
using MediaIsland.Services.MediaLink;
using MediaIsland.Services.MediaLink.Protocol;
using Xunit;

namespace MediaIsland.Tests.MediaLink;

internal sealed class FakeMediaLinkSocket : IMediaLinkSocket
{
    private readonly ConcurrentQueue<MediaLinkSocketMessage> _incoming = new();
    private readonly SemaphoreSlim _incomingSignal = new(0);
    private readonly ConcurrentQueue<string> _outgoing = new();
    private readonly ConcurrentQueue<byte[]> _outgoingBinary = new();
    private int _closed;

    public WebSocketState State => Volatile.Read(ref _closed) == 0 ? WebSocketState.Open : WebSocketState.Closed;

    public IReadOnlyCollection<string> Outgoing => _outgoing.ToArray();

    public IReadOnlyCollection<byte[]> OutgoingBinary => _outgoingBinary.ToArray();

    public void ClearOutgoing()
    {
        while (_outgoing.TryDequeue(out _)) { }
        while (_outgoingBinary.TryDequeue(out _)) { }
    }

    public void EnqueueIncoming(string text)
    {
        _incoming.Enqueue(new MediaLinkSocketMessage(text, null));
        _incomingSignal.Release();
    }

    public void EnqueueIncomingBinary(byte[] data)
    {
        _incoming.Enqueue(new MediaLinkSocketMessage(null, data));
        _incomingSignal.Release();
    }

    /// <summary>
    /// 模拟对端断开：Text 与 Binary 皆为 null 即 IsClosed，接收循环据此退出。
    /// 用于验证「客户端进程被杀」这类不发关闭握手的路径。
    /// </summary>
    public void EnqueueClose()
    {
        _incoming.Enqueue(default);
        _incomingSignal.Release();
    }

    public Task SendTextAsync(string text, CancellationToken cancellationToken)
    {
        _outgoing.Enqueue(text);
        return Task.CompletedTask;
    }

    public Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        _outgoingBinary.Enqueue(data.ToArray());
        return Task.CompletedTask;
    }

    public async Task<MediaLinkSocketMessage> ReceiveAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (_incoming.TryDequeue(out var message))
            {
                return message;
            }

            await _incomingSignal.WaitAsync(cancellationToken);
        }
    }

    public Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _closed, 1);
        return Task.CompletedTask;
    }
}

public class MediaLinkSessionTests
{
    [Fact]
    public async Task UnauthenticatedSubscribe_SendsUnauthorizedAndCloses()
    {
        var socket = new FakeMediaLinkSocket();
        var session = new MediaLinkSession(socket, new MediaLinkSessionOptions { ExpectedToken = "secret" });

        await session.HandleMessageAsync(
            MediaLinkMessageSerializer.Serialize(MediaLinkMessageSerializer.Create(
                MediaLinkProtocol.TypeSubscribe,
                new MediaLinkSubscribePayload { Channels = [MediaLinkProtocol.ChannelMedia] },
                id: "1")),
            CancellationToken.None);

        Assert.Contains(socket.Outgoing, json =>
            json.Contains(MediaLinkProtocol.TypeError, StringComparison.Ordinal) &&
            json.Contains(MediaLinkProtocol.ErrorUnauthorized, StringComparison.Ordinal));
        Assert.Equal(WebSocketState.Closed, socket.State);
        Assert.False(session.IsAuthenticated);
    }

    [Fact]
    public async Task AuthSuccess_SendsAuthOk()
    {
        var socket = new FakeMediaLinkSocket();
        var session = new MediaLinkSession(socket, new MediaLinkSessionOptions { ExpectedToken = "secret" });

        await session.HandleMessageAsync(
            MediaLinkMessageSerializer.Serialize(MediaLinkMessageSerializer.Create(
                MediaLinkProtocol.TypeAuth,
                new MediaLinkAuthPayload { Token = "secret" },
                id: "a1")),
            CancellationToken.None);

        Assert.True(session.IsAuthenticated);
        Assert.Contains(socket.Outgoing, json => json.Contains(MediaLinkProtocol.TypeAuthOk, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AuthFail_Closes()
    {
        var socket = new FakeMediaLinkSocket();
        var session = new MediaLinkSession(socket, new MediaLinkSessionOptions { ExpectedToken = "secret" });

        await session.HandleMessageAsync(
            MediaLinkMessageSerializer.Serialize(MediaLinkMessageSerializer.Create(
                MediaLinkProtocol.TypeAuth,
                new MediaLinkAuthPayload { Token = "nope" })),
            CancellationToken.None);

        Assert.False(session.IsAuthenticated);
        Assert.Contains(socket.Outgoing, json => json.Contains(MediaLinkProtocol.TypeAuthFail, StringComparison.Ordinal));
        Assert.Equal(WebSocketState.Closed, socket.State);
    }

    [Fact]
    public async Task AuthThenSubscribe_InvokesSnapshotCallback()
    {
        var socket = new FakeMediaLinkSocket();
        var snapshotHits = 0;
        var session = new MediaLinkSession(socket, new MediaLinkSessionOptions
        {
            ExpectedToken = "secret",
            OnSubscribedAsync = _ =>
            {
                Interlocked.Increment(ref snapshotHits);
                return Task.CompletedTask;
            }
        });

        await session.HandleMessageAsync(
            MediaLinkMessageSerializer.Serialize(MediaLinkMessageSerializer.Create(
                MediaLinkProtocol.TypeAuth,
                new MediaLinkAuthPayload { Token = "secret" })),
            CancellationToken.None);
        await session.HandleMessageAsync(
            MediaLinkMessageSerializer.Serialize(MediaLinkMessageSerializer.Create(
                MediaLinkProtocol.TypeSubscribe,
                new MediaLinkSubscribePayload
                {
                    Channels = [MediaLinkProtocol.ChannelMedia, MediaLinkProtocol.ChannelLyrics]
                })),
            CancellationToken.None);

        Assert.Equal(1, snapshotHits);
        Assert.True(session.IsSubscribedTo(MediaLinkProtocol.ChannelMedia));
        Assert.True(session.IsSubscribedTo(MediaLinkProtocol.ChannelLyrics));
        Assert.Contains(socket.Outgoing, json => json.Contains(MediaLinkProtocol.TypeSubscribeOk, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Hub_BroadcastsOnlyToSubscribedChannel()
    {
        var mediaSocket = new FakeMediaLinkSocket();
        var lyricsSocket = new FakeMediaLinkSocket();
        var hub = new MediaLinkSessionHub();

        var mediaSession = new MediaLinkSession(mediaSocket, new MediaLinkSessionOptions { ExpectedToken = "t" });
        var lyricsSession = new MediaLinkSession(lyricsSocket, new MediaLinkSessionOptions { ExpectedToken = "t" });
        hub.Add(mediaSession);
        hub.Add(lyricsSession);
        mediaSession.StartWriter(CancellationToken.None);
        lyricsSession.StartWriter(CancellationToken.None);

        await mediaSession.HandleMessageAsync(AuthJson(), CancellationToken.None);
        await mediaSession.HandleMessageAsync(SubscribeJson(MediaLinkProtocol.ChannelMedia), CancellationToken.None);
        await lyricsSession.HandleMessageAsync(AuthJson(), CancellationToken.None);
        await lyricsSession.HandleMessageAsync(SubscribeJson(MediaLinkProtocol.ChannelLyrics), CancellationToken.None);

        mediaSocket.ClearOutgoing();
        lyricsSocket.ClearOutgoing();

        await hub.BroadcastEventAsync(
            MediaLinkProtocol.ChannelMedia,
            MediaLinkProtocol.EventMediaUpdated,
            new MediaLinkMediaDto { Title = "only-media" });
        await Task.Delay(100);

        Assert.NotEmpty(mediaSocket.Outgoing);
        Assert.Empty(lyricsSocket.Outgoing);
        Assert.Contains(mediaSocket.Outgoing, json => json.Contains("only-media", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unsubscribe_RemovesChannelAndSendsOk()
    {
        var socket = new FakeMediaLinkSocket();
        var session = new MediaLinkSession(socket, new MediaLinkSessionOptions { ExpectedToken = "t" });

        await session.HandleMessageAsync(AuthJson(), CancellationToken.None);
        await session.HandleMessageAsync(
            SubscribeJson(MediaLinkProtocol.ChannelMedia, MediaLinkProtocol.ChannelLyrics),
            CancellationToken.None);

        Assert.True(session.IsSubscribedTo(MediaLinkProtocol.ChannelMedia));
        Assert.True(session.IsSubscribedTo(MediaLinkProtocol.ChannelLyrics));

        socket.ClearOutgoing();
        await session.HandleMessageAsync(
            MediaLinkMessageSerializer.Serialize(MediaLinkMessageSerializer.Create(
                MediaLinkProtocol.TypeUnsubscribe,
                new MediaLinkUnsubscribePayload { Channels = [MediaLinkProtocol.ChannelLyrics] },
                id: "unsub-1")),
            CancellationToken.None);

        Assert.True(session.IsSubscribedTo(MediaLinkProtocol.ChannelMedia));
        Assert.False(session.IsSubscribedTo(MediaLinkProtocol.ChannelLyrics));
        Assert.Contains(socket.Outgoing, json =>
            json.Contains(MediaLinkProtocol.TypeUnsubscribeOk, StringComparison.Ordinal) &&
            json.Contains("unsub-1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unsubscribe_AllChannels_ThenReceivesNoBroadcasts()
    {
        var socket = new FakeMediaLinkSocket();
        var session = new MediaLinkSession(socket, new MediaLinkSessionOptions { ExpectedToken = "t" });
        var hub = new MediaLinkSessionHub();
        hub.Add(session);
        session.StartWriter(CancellationToken.None);

        await session.HandleMessageAsync(AuthJson(), CancellationToken.None);
        await session.HandleMessageAsync(SubscribeJson(MediaLinkProtocol.ChannelMedia), CancellationToken.None);

        socket.ClearOutgoing();
        await session.HandleMessageAsync(
            MediaLinkMessageSerializer.Serialize(MediaLinkMessageSerializer.Create(
                MediaLinkProtocol.TypeUnsubscribe,
                new MediaLinkUnsubscribePayload { Channels = [MediaLinkProtocol.ChannelMedia] },
                id: "unsub-all")),
            CancellationToken.None);

        Assert.False(session.IsSubscribedTo(MediaLinkProtocol.ChannelMedia));
        await Task.Delay(50);
        Assert.Contains(socket.Outgoing, json => json.Contains(MediaLinkProtocol.TypeUnsubscribeOk, StringComparison.Ordinal));

        socket.ClearOutgoing();
        await hub.BroadcastEventAsync(
            MediaLinkProtocol.ChannelMedia,
            MediaLinkProtocol.EventMediaUpdated,
            new MediaLinkMediaDto { Title = "should-not-arrive" });

        Assert.Empty(socket.Outgoing);
    }

    [Fact]
    public async Task Unsubscribe_NotSubscribed_SendsBadRequest()
    {
        var socket = new FakeMediaLinkSocket();
        var session = new MediaLinkSession(socket, new MediaLinkSessionOptions { ExpectedToken = "t" });

        await session.HandleMessageAsync(AuthJson(), CancellationToken.None);
        await session.HandleMessageAsync(SubscribeJson(MediaLinkProtocol.ChannelMedia), CancellationToken.None);

        socket.ClearOutgoing();
        await session.HandleMessageAsync(
            MediaLinkMessageSerializer.Serialize(MediaLinkMessageSerializer.Create(
                MediaLinkProtocol.TypeUnsubscribe,
                new MediaLinkUnsubscribePayload { Channels = [MediaLinkProtocol.ChannelLyrics] },
                id: "unsub-fail")),
            CancellationToken.None);

        Assert.Contains(socket.Outgoing, json =>
            json.Contains(MediaLinkProtocol.TypeError, StringComparison.Ordinal) &&
            json.Contains(MediaLinkProtocol.ErrorBadRequest, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unsubscribe_InvalidChannel_SendsBadRequest()
    {
        var socket = new FakeMediaLinkSocket();
        var session = new MediaLinkSession(socket, new MediaLinkSessionOptions { ExpectedToken = "t" });

        await session.HandleMessageAsync(AuthJson(), CancellationToken.None);

        socket.ClearOutgoing();
        await session.HandleMessageAsync(
            MediaLinkMessageSerializer.Serialize(MediaLinkMessageSerializer.Create(
                MediaLinkProtocol.TypeUnsubscribe,
                new MediaLinkUnsubscribePayload { Channels = ["nonexistent"] },
                id: "unsub-bad")),
            CancellationToken.None);

        Assert.Contains(socket.Outgoing, json =>
            json.Contains(MediaLinkProtocol.TypeError, StringComparison.Ordinal) &&
            json.Contains(MediaLinkProtocol.ErrorBadRequest, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unsubscribe_PreAuth_SendsUnauthorizedAndCloses()
    {
        var socket = new FakeMediaLinkSocket();
        var session = new MediaLinkSession(socket, new MediaLinkSessionOptions { ExpectedToken = "t" });

        await session.HandleMessageAsync(
            MediaLinkMessageSerializer.Serialize(MediaLinkMessageSerializer.Create(
                MediaLinkProtocol.TypeUnsubscribe,
                new MediaLinkUnsubscribePayload { Channels = [MediaLinkProtocol.ChannelMedia] },
                id: "unsub-preauth")),
            CancellationToken.None);

        Assert.Contains(socket.Outgoing, json =>
            json.Contains(MediaLinkProtocol.ErrorUnauthorized, StringComparison.Ordinal));
        Assert.Equal(WebSocketState.Closed, socket.State);
    }

        [Fact]
    public async Task QueueOverflow_DropsOldestMedia_WhenFull()
    {
        var socket = new FakeMediaLinkSocket();
        var session = new MediaLinkSession(socket, new MediaLinkSessionOptions { ExpectedToken = "t" });
        session.StartWriter(CancellationToken.None);

        await session.HandleMessageAsync(AuthJson(), CancellationToken.None);
        await session.HandleMessageAsync(
            MediaLinkMessageSerializer.Serialize(MediaLinkMessageSerializer.Create(
                MediaLinkProtocol.TypeSubscribe,
                new MediaLinkSubscribePayload { Channels = [MediaLinkProtocol.ChannelMedia, MediaLinkProtocol.ChannelLyrics] },
                id: "s1")),
            CancellationToken.None);

        // Enqueue many media.updated without draining -> oldest dropped, no crash
        for (var i = 0; i < 200; i++)
        {
            await session.EnqueueAsync(
                MediaLinkMessageSerializer.Create(MediaLinkProtocol.TypeEvent,
                    new MediaLinkMediaDto { Title = $"fill-{i}", SourceApp = "test", PlaybackState = "Playing", ChangeKind = "Timeline" },
                    name: MediaLinkProtocol.EventMediaUpdated,
                    seq: i),
                droppable: true);
        }

        // Enqueue lyrics.updated -> succeeds (media dropped to make room)
        await session.EnqueueAsync(
            MediaLinkMessageSerializer.Create(MediaLinkProtocol.TypeEvent,
                new MediaLinkLyricsDto { Id = "ly-1", Title = "Test", Artist = "A", DurationMs = 1000, Source = "External" },
                name: MediaLinkProtocol.EventLyricsUpdated),
            droppable: false);

        await Task.Delay(200);
        // Session should NOT be closed - media was dropped to make room
        Assert.False(session.IsClosed);
        // Writer should have processed some items
        Assert.NotEmpty(socket.Outgoing);
    }
private static string AuthJson() =>
        MediaLinkMessageSerializer.Serialize(MediaLinkMessageSerializer.Create(
            MediaLinkProtocol.TypeAuth,
            new MediaLinkAuthPayload { Token = "t" }));

    private static string SubscribeJson(params string[] channels) =>
        MediaLinkMessageSerializer.Serialize(MediaLinkMessageSerializer.Create(
            MediaLinkProtocol.TypeSubscribe,
            new MediaLinkSubscribePayload { Channels = channels.ToList() }));
}