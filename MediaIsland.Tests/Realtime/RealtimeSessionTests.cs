using System.Net.WebSockets;
using System.Collections.Concurrent;
using MediaIsland.Services.Realtime;
using MediaIsland.Services.Realtime.Protocol;
using Xunit;

namespace MediaIsland.Tests.Realtime;

internal sealed class FakeRealtimeSocket : IRealtimeSocket
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

public class RealtimeSessionTests
{
    [Fact]
    public async Task UnauthenticatedSubscribe_SendsUnauthorizedAndCloses()
    {
        var socket = new FakeRealtimeSocket();
        var session = new RealtimeSession(socket, new RealtimeSessionOptions { ExpectedToken = "secret" });

        await session.HandleMessageAsync(
            RealtimeMessageSerializer.Serialize(RealtimeMessageSerializer.Create(
                RealtimeProtocol.TypeSubscribe,
                new RealtimeSubscribePayload { Channels = [RealtimeProtocol.ChannelMedia] },
                id: "1")),
            CancellationToken.None);

        Assert.Contains(socket.Outgoing, json =>
            json.Contains(RealtimeProtocol.TypeError, StringComparison.Ordinal) &&
            json.Contains(RealtimeProtocol.ErrorUnauthorized, StringComparison.Ordinal));
        Assert.Equal(WebSocketState.Closed, socket.State);
        Assert.False(session.IsAuthenticated);
    }

    [Fact]
    public async Task AuthSuccess_SendsAuthOk()
    {
        var socket = new FakeRealtimeSocket();
        var session = new RealtimeSession(socket, new RealtimeSessionOptions { ExpectedToken = "secret" });

        await session.HandleMessageAsync(
            RealtimeMessageSerializer.Serialize(RealtimeMessageSerializer.Create(
                RealtimeProtocol.TypeAuth,
                new RealtimeAuthPayload { Token = "secret" },
                id: "a1")),
            CancellationToken.None);

        Assert.True(session.IsAuthenticated);
        Assert.Contains(socket.Outgoing, json => json.Contains(RealtimeProtocol.TypeAuthOk, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AuthFail_Closes()
    {
        var socket = new FakeRealtimeSocket();
        var session = new RealtimeSession(socket, new RealtimeSessionOptions { ExpectedToken = "secret" });

        await session.HandleMessageAsync(
            RealtimeMessageSerializer.Serialize(RealtimeMessageSerializer.Create(
                RealtimeProtocol.TypeAuth,
                new RealtimeAuthPayload { Token = "nope" })),
            CancellationToken.None);

        Assert.False(session.IsAuthenticated);
        Assert.Contains(socket.Outgoing, json => json.Contains(RealtimeProtocol.TypeAuthFail, StringComparison.Ordinal));
        Assert.Equal(WebSocketState.Closed, socket.State);
    }

    [Fact]
    public async Task AuthThenSubscribe_InvokesSnapshotCallback()
    {
        var socket = new FakeRealtimeSocket();
        var snapshotHits = 0;
        var session = new RealtimeSession(socket, new RealtimeSessionOptions
        {
            ExpectedToken = "secret",
            OnSubscribedAsync = _ =>
            {
                Interlocked.Increment(ref snapshotHits);
                return Task.CompletedTask;
            }
        });

        await session.HandleMessageAsync(
            RealtimeMessageSerializer.Serialize(RealtimeMessageSerializer.Create(
                RealtimeProtocol.TypeAuth,
                new RealtimeAuthPayload { Token = "secret" })),
            CancellationToken.None);
        await session.HandleMessageAsync(
            RealtimeMessageSerializer.Serialize(RealtimeMessageSerializer.Create(
                RealtimeProtocol.TypeSubscribe,
                new RealtimeSubscribePayload
                {
                    Channels = [RealtimeProtocol.ChannelMedia, RealtimeProtocol.ChannelLyrics]
                })),
            CancellationToken.None);

        Assert.Equal(1, snapshotHits);
        Assert.True(session.IsSubscribedTo(RealtimeProtocol.ChannelMedia));
        Assert.True(session.IsSubscribedTo(RealtimeProtocol.ChannelLyrics));
        Assert.Contains(socket.Outgoing, json => json.Contains(RealtimeProtocol.TypeSubscribeOk, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Hub_BroadcastsOnlyToSubscribedChannel()
    {
        var mediaSocket = new FakeRealtimeSocket();
        var lyricsSocket = new FakeRealtimeSocket();
        var hub = new RealtimeSessionHub();

        var mediaSession = new RealtimeSession(mediaSocket, new RealtimeSessionOptions { ExpectedToken = "t" });
        var lyricsSession = new RealtimeSession(lyricsSocket, new RealtimeSessionOptions { ExpectedToken = "t" });
        hub.Add(mediaSession);
        hub.Add(lyricsSession);

        await mediaSession.HandleMessageAsync(AuthJson(), CancellationToken.None);
        await mediaSession.HandleMessageAsync(SubscribeJson(RealtimeProtocol.ChannelMedia), CancellationToken.None);
        await lyricsSession.HandleMessageAsync(AuthJson(), CancellationToken.None);
        await lyricsSession.HandleMessageAsync(SubscribeJson(RealtimeProtocol.ChannelLyrics), CancellationToken.None);

        mediaSocket.ClearOutgoing();
        lyricsSocket.ClearOutgoing();

        await hub.BroadcastEventAsync(
            RealtimeProtocol.ChannelMedia,
            RealtimeProtocol.EventMediaUpdated,
            new RealtimeMediaDto { Title = "only-media" });

        Assert.NotEmpty(mediaSocket.Outgoing);
        Assert.Empty(lyricsSocket.Outgoing);
        Assert.Contains(mediaSocket.Outgoing, json => json.Contains("only-media", StringComparison.Ordinal));
    }

    private static string AuthJson() =>
        RealtimeMessageSerializer.Serialize(RealtimeMessageSerializer.Create(
            RealtimeProtocol.TypeAuth,
            new RealtimeAuthPayload { Token = "t" }));

    private static string SubscribeJson(string channel) =>
        RealtimeMessageSerializer.Serialize(RealtimeMessageSerializer.Create(
            RealtimeProtocol.TypeSubscribe,
            new RealtimeSubscribePayload { Channels = [channel] }));
}

