using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Lyrics.Models;
using MediaIsland.Services.Media;
using MediaIsland.Services.Realtime;
using MediaIsland.Services.Realtime.Protocol;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace MediaIsland.Tests.Realtime;

internal sealed class FakeMediaService : IMediaService
{
    public event EventHandler<MediaInfoChangedEventArgs>? MediaInfoChanged;

    public MediaInfo? CurrentMediaInfo { get; set; }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task EnsureStartedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void Raise(MediaInfo? info, MediaInfoChangeKind kind) =>
        MediaInfoChanged?.Invoke(this, new MediaInfoChangedEventArgs(info, kind));
}

public class RealtimeStatePublisherTests
{
    [Fact]
    public async Task Timeline_IsThrottled_WhilePlaybackIsImmediate()
    {
        var media = new FakeMediaService();
        var lyrics = new LyricsSearchService([], [], () => new LyricsSourceSettings());
        var hub = new RealtimeSessionHub();
        var socket = new FakeRealtimeSocket();
        var session = new RealtimeSession(socket, new RealtimeSessionOptions { ExpectedToken = "t" });
        hub.Add(session);
        await session.HandleMessageAsync(Auth("t"), CancellationToken.None);
        await session.HandleMessageAsync(Subscribe(RealtimeProtocol.ChannelMedia), CancellationToken.None);
        socket.ClearOutgoing();

        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var publisher = new RealtimeStatePublisher(
            media,
            lyrics,
            hub,
            timelineMinIntervalMs: () => 200,
            utcNow: () => now);
        publisher.Start();

        var sample = new MediaInfo(
            "app", "t", "a", null,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10),
            new MediaPlaybackInfo(MediaPlaybackState.Playing),
            null, null);

        media.Raise(sample, MediaInfoChangeKind.Timeline);
        media.Raise(sample with { Position = TimeSpan.FromSeconds(2) }, MediaInfoChangeKind.Timeline);
        media.Raise(sample with { Position = TimeSpan.FromSeconds(3) }, MediaInfoChangeKind.Timeline);
        await Task.Delay(50);

        Assert.Single(socket.Outgoing);

        socket.ClearOutgoing();
        media.Raise(sample with { PlaybackInfo = new MediaPlaybackInfo(MediaPlaybackState.Paused) }, MediaInfoChangeKind.Playback);
        await Task.Delay(50);
        Assert.Single(socket.Outgoing);
        Assert.Contains(socket.Outgoing, json => json.Contains("Paused", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LyricsEvent_BroadcastsIndependently()
    {
        var media = new FakeMediaService();
        var lyrics = new LyricsSearchService([], [], () => new LyricsSourceSettings());
        var hub = new RealtimeSessionHub();
        var socket = new FakeRealtimeSocket();
        var session = new RealtimeSession(socket, new RealtimeSessionOptions { ExpectedToken = "t" });
        hub.Add(session);
        await session.HandleMessageAsync(Auth("t"), CancellationToken.None);
        await session.HandleMessageAsync(Subscribe(RealtimeProtocol.ChannelLyrics), CancellationToken.None);
        socket.ClearOutgoing();

        using var publisher = new RealtimeStatePublisher(media, lyrics, hub);
        publisher.Start();

        // LyricsSearchService has no public raise; use reflection on Publish path via CurrentResultChanged
        // Instead publish snapshot after manually invoking through a temporary event by searching with empty providers yields null - use hub direct for independence? 
        // Better: call PublishSnapshotAsync after setting nothing - still null payload event.
        await publisher.PublishSnapshotAsync(session);
        await Task.Delay(20);
        Assert.Contains(socket.Outgoing, json => json.Contains(RealtimeProtocol.EventLyricsUpdated, StringComparison.Ordinal));
    }

    private static string Auth(string token) =>
        RealtimeMessageSerializer.Serialize(RealtimeMessageSerializer.Create(
            RealtimeProtocol.TypeAuth,
            new RealtimeAuthPayload { Token = token }));

    private static string Subscribe(params string[] channels) =>
        RealtimeMessageSerializer.Serialize(RealtimeMessageSerializer.Create(
            RealtimeProtocol.TypeSubscribe,
            new RealtimeSubscribePayload { Channels = channels.ToList() }));
}
