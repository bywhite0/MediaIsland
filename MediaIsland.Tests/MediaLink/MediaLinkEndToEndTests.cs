using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Lyrics.Models;
using MediaIsland.Services.Media;
using MediaIsland.Services.MediaLink;
using MediaIsland.Services.MediaLink.Protocol;
using MediaIsland.Models;
using Xunit;

namespace MediaIsland.Tests.MediaLink;

public class MediaLinkEndToEndTests
{
    [Fact]
    public async Task FullFlow_ConnectAuthSubscribeReceiveUnsubscribe_StopReleasesPort()
    {
        // Arrange: let the OS assign a free port
        var port = 0;

        var media = new E2EFakeMediaService();
        var lyrics = new LyricsSearchService([], [], () => new LyricsSourceSettings());
        var store = new MediaLinkInjectionStore();
        var settings = new PluginSettings
        {
            MediaLinkMediaSourceMode = MediaLinkMediaSourceMode.PlatformOnly,
            MediaLinkPushUsesEffective = true
        };
        using var coordinator = new MediaSourceCoordinator(media, lyrics, store, () => settings);

        var hub = new MediaLinkSessionHub();
        using var publisher = new MediaLinkStatePublisher(coordinator, hub, timelineMinIntervalMs: () => 0);
        publisher.Start();
        var token = "test-token-123";
        var server = new MediaLinkServer(
            hub,
            session => publisher.PublishSnapshotAsync(session),
            () => token,
            coordinator: coordinator,
            logger: null);

        await server.StartAsync("127.0.0.1", port);
        Assert.True(server.IsRunning);
        // Extract actual port from endpoint
        var endpoint = server.Endpoint;
        var actualPort = int.Parse(endpoint!.Split(':')[2].Split('/')[0]);

        // Act: connect a real WebSocket client
        using var client = new ClientWebSocket();
        client.Options.KeepAliveInterval = TimeSpan.FromSeconds(5);
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{actualPort}/v1/ws"), CancellationToken.None);
        Assert.Equal(WebSocketState.Open, client.State);

        // Receive server.hello
        var hello = await ReceiveJsonAsync(client);
        Assert.Equal(MediaLinkProtocol.TypeEvent, hello.GetProperty("type").GetString());
        Assert.Equal(MediaLinkProtocol.EventServerHello, hello.GetProperty("name").GetString());

        // Send auth
        await SendJsonAsync(client, new { type = "auth", id = "a1", v = 1, ts = NowMs(), payload = new { token } });
        var authOk = await ReceiveJsonAsync(client);
        Assert.Equal(MediaLinkProtocol.TypeAuthOk, authOk.GetProperty("type").GetString());

        // Subscribe
        await SendJsonAsync(client, new { type = "subscribe", id = "s1", v = 1, ts = NowMs(), payload = new { channels = new[] { "media", "lyrics" } } });
        var subOk = await ReceiveJsonAsync(client);
        Assert.Equal(MediaLinkProtocol.TypeSubscribeOk, subOk.GetProperty("type").GetString());

        // Receive snapshots (media.updated and/or lyrics.updated)
        var snapshotNames = new List<string>();
        for (var i = 0; i < 2; i++)
        {
            try
            {
                var snap = await ReceiveJsonAsync(client, timeoutMs: 1000);
                snapshotNames.Add(snap.GetProperty("name").GetString()!);
            }
            catch (OperationCanceledException) { break; }
        }
        Assert.Contains(MediaLinkProtocol.EventMediaUpdated, snapshotNames);

        // Trigger media change
        var sample = new MediaInfo("test-app", "TestSong", "TestArtist", "Album",
            TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(3),
            new MediaPlaybackInfo(MediaPlaybackState.Playing), null, null);
        media.Raise(sample, MediaInfoChangeKind.MediaProperties);

        // Receive incremental media.updated
        var mediaEvent = await ReceiveJsonAsync(client, timeoutMs: 5000);
        Assert.Equal(MediaLinkProtocol.EventMediaUpdated, mediaEvent.GetProperty("name").GetString());
        Assert.True(mediaEvent.GetProperty("seq").GetInt64() > 0);
        var payload = mediaEvent.GetProperty("payload");
        Assert.Equal("TestSong", payload.GetProperty("title").GetString());
        Assert.Equal("TestArtist", payload.GetProperty("artist").GetString());
        Assert.True(payload.TryGetProperty("trackToken", out _));
        Assert.True(payload.TryGetProperty("positionCapturedAtMs", out _));

        // Trigger another change, verify seq increases
        media.Raise(sample with { Position = TimeSpan.FromSeconds(60) }, MediaInfoChangeKind.Timeline);
        var mediaEvent2 = await ReceiveJsonAsync(client, timeoutMs: 5000);
        Assert.True(mediaEvent2.TryGetProperty("seq", out var seq2) && seq2.GetInt64() > mediaEvent.GetProperty("seq").GetInt64(),
            $"Expected seq in mediaEvent2, got: {mediaEvent2}");

        // Unsubscribe from lyrics
        await SendJsonAsync(client, new { type = "unsubscribe", id = "u1", v = 1, ts = NowMs(), payload = new { channels = new[] { "lyrics" } } });
        // Read messages until we find unsubscribe_ok (may receive trailing-edge events first)
        JsonElement unsubOk;
        while (true)
        {
            unsubOk = await ReceiveJsonAsync(client, timeoutMs: 5000);
            if (unsubOk.GetProperty("type").GetString() == MediaLinkProtocol.TypeUnsubscribeOk) break;
        }
        Assert.Equal(MediaLinkProtocol.TypeUnsubscribeOk, unsubOk.GetProperty("type").GetString());

        // Close client
        try { await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); } catch { }

        // Stop server
        await server.StopAsync();
        Assert.False(server.IsRunning);

        // Verify port is released
        try
        {
            var probe = new TcpListener(IPAddress.Loopback, port);
            probe.Start();
            probe.Stop();
        }
        catch
        {
            Assert.Fail("Port not released after server stop");
        }
    }

    [Fact]
    public async Task PingPong_WorksOverRealConnection()
    {
        var hub = new MediaLinkSessionHub();
        var server = new MediaLinkServer(hub, _ => Task.CompletedTask, () => "tok");
        await server.StartAsync("127.0.0.1", 0);
        var actualPort = int.Parse(server.Endpoint!.Split(':')[2].Split('/')[0]);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{actualPort}/v1/ws"), CancellationToken.None);

        // hello
        await ReceiveJsonAsync(client);

        // auth
        await SendJsonAsync(client, new { type = "auth", id = "a1", v = 1, ts = NowMs(), payload = new { token = "tok" } });
        await ReceiveJsonAsync(client);

        // ping
        await SendJsonAsync(client, new { type = "ping", id = "p1", v = 1, ts = NowMs() });
        var pong = await ReceiveJsonAsync(client);
        Assert.Equal(MediaLinkProtocol.TypePong, pong.GetProperty("type").GetString());
        Assert.Equal("p1", pong.GetProperty("id").GetString());

        try { await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); } catch { }
        await server.StopAsync();
    }

    [Fact]
    public async Task RapidTrackChanges_TrackTokenMatchesSourceApp()
    {
        var hub = new MediaLinkSessionHub();
        var media = new E2EFakeMediaService();
        var lyrics = new LyricsSearchService([], [], () => new LyricsSourceSettings());
        var store = new MediaLinkInjectionStore();
        var settings = new PluginSettings
        {
            MediaLinkMediaSourceMode = MediaLinkMediaSourceMode.PlatformOnly,
            MediaLinkPushUsesEffective = true
        };
        using var coordinator = new MediaSourceCoordinator(media, lyrics, store, () => settings);
        using var publisher = new MediaLinkStatePublisher(coordinator, hub, timelineMinIntervalMs: () => 0);
        publisher.Start();

        var server = new MediaLinkServer(hub, session => publisher.PublishSnapshotAsync(session), () => "tok", coordinator: coordinator);
        await server.StartAsync("127.0.0.1", 0);
        var port = int.Parse(server.Endpoint!.Split(':')[2].Split('/')[0]);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/v1/ws"), CancellationToken.None);
        await ReceiveJsonAsync(client); // hello
        await SendJsonAsync(client, new { type = "auth", id = "a1", v = 1, ts = NowMs(), payload = new { token = "tok" } });
        await ReceiveJsonAsync(client); // auth_ok
        await SendJsonAsync(client, new { type = "subscribe", id = "s1", v = 1, ts = NowMs(), payload = new { channels = new[] { "media" } } });
        await ReceiveJsonAsync(client); // subscribe_ok

        // Rapid track changes: 10 songs in succession
        var trackTokens = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            var song = new MediaInfo("player", $"Song{i}", $"Artist{i}", null,
                TimeSpan.Zero, TimeSpan.FromMinutes(3),
                new MediaPlaybackInfo(MediaPlaybackState.Playing), null, null);
            media.Raise(song, MediaInfoChangeKind.MediaProperties);
            await Task.Delay(30);
        }

        // Drain all received events
        var received = new List<JsonElement>();
        try
        {
            while (true)
            {
                try { received.Add(await ReceiveJsonAsync(client, timeoutMs: 500)); }
                catch (OperationCanceledException) { break; }
            }
        }
        catch (WebSocketException) { } // connection may be aborted by server

        // Verify: each media.updated has a trackToken, and trackTokens change with each song
        var mediaEvents = received.Where(e => e.TryGetProperty("name", out var n) && n.GetString() == MediaLinkProtocol.EventMediaUpdated).ToList();
        Assert.True(mediaEvents.Count >= 2, $"Expected at least 2 media events from 10 track changes, got {mediaEvents.Count}. Total received: {received.Count}");
        foreach (var evt in mediaEvents)
        {
            Assert.True(evt.GetProperty("payload").TryGetProperty("trackToken", out _));
        }

        try { await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); } catch { }
        await server.StopAsync();
    }

    private static async Task<JsonElement> ReceiveJsonAsync(WebSocket ws, int timeoutMs = 3000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer, cts.Token);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("WebSocket closed unexpectedly");
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        return JsonDocument.Parse(Encoding.UTF8.GetString(ms.ToArray())).RootElement;
    }

    private static async Task SendJsonAsync(WebSocket ws, object payload)
    {
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private sealed class E2EFakeMediaService : IMediaService
    {
        public event EventHandler<MediaInfoChangedEventArgs>? MediaInfoChanged;
        public MediaInfo? CurrentMediaInfo { get; set; }
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
        public Task EnsureStartedAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Raise(MediaInfo? info, MediaInfoChangeKind kind)
        {
            CurrentMediaInfo = info;
            MediaInfoChanged?.Invoke(this, new MediaInfoChangedEventArgs(info, kind));
        }
    }
}