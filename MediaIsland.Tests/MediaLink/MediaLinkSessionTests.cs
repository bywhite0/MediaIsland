using System.Net.WebSockets;
using System.Collections.Concurrent;
using MediaIsland.Services.MediaLink;
using MediaIsland.Services.MediaLink.Protocol;
using Xunit;

namespace MediaIsland.Tests.MediaLink;

internal sealed class FakeMediaLinkSocket : IMediaLinkSocket
{
    private readonly ConcurrentQueue<string> _incoming = new();
    private readonly SemaphoreSlim _incomingSignal = new(0);
    private readonly ConcurrentQueue<string> _outgoing = new();
    private int _closed;

    public WebSocketState State => Volatile.Read(ref _closed) == 0 ? WebSocketState.Open : WebSocketState.Closed;

    public IReadOnlyCollection<string> Outgoing => _outgoing.ToArray();

    public void ClearOutgoing()
    {
        while (_outgoing.TryDequeue(out _)) { }
    }

    public void EnqueueIncoming(string text)
    {
        _incoming.Enqueue(text);
        _incomingSignal.Release();
    }

    public Task SendTextAsync(string text, CancellationToken cancellationToken)
    {
        _outgoing.Enqueue(text);
        return Task.CompletedTask;
    }

    public async Task<string?> ReceiveTextAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (_incoming.TryDequeue(out var text))
            {
                return text;
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

        Assert.NotEmpty(mediaSocket.Outgoing);
        Assert.Empty(lyricsSocket.Outgoing);
        Assert.Contains(mediaSocket.Outgoing, json => json.Contains("only-media", StringComparison.Ordinal));
    }

    private static string AuthJson() =>
        MediaLinkMessageSerializer.Serialize(MediaLinkMessageSerializer.Create(
            MediaLinkProtocol.TypeAuth,
            new MediaLinkAuthPayload { Token = "t" }));

    private static string SubscribeJson(string channel) =>
        MediaLinkMessageSerializer.Serialize(MediaLinkMessageSerializer.Create(
            MediaLinkProtocol.TypeSubscribe,
            new MediaLinkSubscribePayload { Channels = [channel] }));
}

