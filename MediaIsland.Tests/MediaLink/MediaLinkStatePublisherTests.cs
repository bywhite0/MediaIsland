using MediaIsland.Models;
using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Lyrics.Models;
using MediaIsland.Services.Media;
using MediaIsland.Services.MediaLink;
using MediaIsland.Services.MediaLink.Protocol;
using Xunit;

namespace MediaIsland.Tests.MediaLink;

internal sealed class FakeMediaService : IMediaService
{
    public event EventHandler<MediaInfoChangedEventArgs>? MediaInfoChanged;

    public MediaInfo? CurrentMediaInfo { get; set; }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task EnsureStartedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void Raise(MediaInfo? info, MediaInfoChangeKind kind)
    {
        CurrentMediaInfo = info;
        MediaInfoChanged?.Invoke(this, new MediaInfoChangedEventArgs(info, kind));
    }
}

public class MediaLinkStatePublisherTests
{
    [Fact]
    public async Task Timeline_IsThrottled_WhilePlaybackIsImmediate()
    {
        var media = new FakeMediaService();
        var lyrics = new LyricsSearchService([], [], () => new LyricsSourceSettings());
        var store = new MediaLinkInjectionStore();
        var settings = new PluginSettings
        {
            MediaLinkMediaSourceMode = MediaLinkMediaSourceMode.PlatformOnly,
            MediaLinkPushUsesEffective = true
        };
        using var coordinator = new MediaSourceCoordinator(media, lyrics, store, () => settings);

        var hub = new MediaLinkSessionHub();
        var socket = new FakeMediaLinkSocket();
        var session = new MediaLinkSession(socket, new MediaLinkSessionOptions { ExpectedToken = "t" });
        hub.Add(session);
        session.StartWriter(CancellationToken.None);
        await session.HandleMessageAsync(Auth("t"), CancellationToken.None);
        await session.HandleMessageAsync(Subscribe(MediaLinkProtocol.ChannelMedia), CancellationToken.None);
        socket.ClearOutgoing();

        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var publisher = new MediaLinkStatePublisher(
            coordinator,
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
        await Task.Delay(100);

        Assert.Single(socket.Outgoing);

        socket.ClearOutgoing();
        media.Raise(
            sample with { PlaybackInfo = new MediaPlaybackInfo(MediaPlaybackState.Paused) },
            MediaInfoChangeKind.Playback);
        await Task.Delay(50);
        Assert.Single(socket.Outgoing);
        Assert.Contains(socket.Outgoing, json => json.Contains("Paused", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LyricsEvent_BroadcastsIndependently()
    {
        var media = new FakeMediaService();
        var lyrics = new LyricsSearchService([], [], () => new LyricsSourceSettings());
        var store = new MediaLinkInjectionStore();
        var settings = new PluginSettings
        {
            MediaLinkMediaSourceMode = MediaLinkMediaSourceMode.PlatformOnly,
            MediaLinkPushUsesEffective = true
        };
        using var coordinator = new MediaSourceCoordinator(media, lyrics, store, () => settings);

        var hub = new MediaLinkSessionHub();
        var socket = new FakeMediaLinkSocket();
        var session = new MediaLinkSession(socket, new MediaLinkSessionOptions { ExpectedToken = "t" });
        hub.Add(session);
        session.StartWriter(CancellationToken.None);
        await session.HandleMessageAsync(Auth("t"), CancellationToken.None);
        await session.HandleMessageAsync(Subscribe(MediaLinkProtocol.ChannelLyrics), CancellationToken.None);
        socket.ClearOutgoing();

        using var publisher = new MediaLinkStatePublisher(coordinator, hub);
        publisher.Start();

        await publisher.PublishSnapshotAsync(session);
        await Task.Delay(100);
        Assert.Contains(socket.Outgoing, json => json.Contains(MediaLinkProtocol.EventLyricsUpdated, StringComparison.Ordinal));
    }

    [Fact]
    public async Task PublishSnapshot_UsesPushEffective_ExternalOnly()
    {
        var media = new FakeMediaService
        {
            CurrentMediaInfo = new MediaInfo(
                "app", "platform", "a", null,
                TimeSpan.Zero, TimeSpan.FromMinutes(1),
                new MediaPlaybackInfo(MediaPlaybackState.Playing), null, null)
        };
        var lyrics = new LyricsSearchService([], [], () => new LyricsSourceSettings());
        var store = new MediaLinkInjectionStore();
        store.TrySetMedia(new MediaLinkMediaInjectPayload
        {
            Title = "ext", PlaybackState = "Paused"
        }, out _);
        var settings = new PluginSettings
        {
            MediaLinkMediaSourceMode = MediaLinkMediaSourceMode.ExternalOnly,
            MediaLinkPushUsesEffective = true
        };
        using var coordinator = new MediaSourceCoordinator(media, lyrics, store, () => settings);
        coordinator.Recompute();

        var hub = new MediaLinkSessionHub();
        var socket = new FakeMediaLinkSocket();
        var session = new MediaLinkSession(socket, new MediaLinkSessionOptions { ExpectedToken = "t" });
        hub.Add(session);
        session.StartWriter(CancellationToken.None);
        await session.HandleMessageAsync(Auth("t"), CancellationToken.None);
        await session.HandleMessageAsync(Subscribe(MediaLinkProtocol.ChannelMedia), CancellationToken.None);
        socket.ClearOutgoing();

        using var publisher = new MediaLinkStatePublisher(coordinator, hub);
        await publisher.PublishSnapshotAsync(session);
        await Task.Delay(100);
        Assert.Contains(socket.Outgoing, j => j.Contains("ext"));
        Assert.DoesNotContain(socket.Outgoing, j => j.Contains("platform"));
    }

    [Fact]
    public async Task PublishSnapshot_PushFlagFalse_UsesPlatform()
    {
        var media = new FakeMediaService
        {
            CurrentMediaInfo = new MediaInfo(
                "app", "platform", "a", null,
                TimeSpan.Zero, TimeSpan.FromMinutes(1),
                new MediaPlaybackInfo(MediaPlaybackState.Playing), null, null)
        };
        var lyrics = new LyricsSearchService([], [], () => new LyricsSourceSettings());
        var store = new MediaLinkInjectionStore();
        store.TrySetMedia(new MediaLinkMediaInjectPayload
        {
            Title = "ext", PlaybackState = "Paused"
        }, out _);
        var settings = new PluginSettings
        {
            MediaLinkMediaSourceMode = MediaLinkMediaSourceMode.ExternalOnly,
            MediaLinkPushUsesEffective = false
        };
        using var coordinator = new MediaSourceCoordinator(media, lyrics, store, () => settings);
        coordinator.Recompute();

        var hub = new MediaLinkSessionHub();
        var socket = new FakeMediaLinkSocket();
        var session = new MediaLinkSession(socket, new MediaLinkSessionOptions { ExpectedToken = "t" });
        hub.Add(session);
        session.StartWriter(CancellationToken.None);
        await session.HandleMessageAsync(Auth("t"), CancellationToken.None);
        await session.HandleMessageAsync(Subscribe(MediaLinkProtocol.ChannelMedia), CancellationToken.None);
        socket.ClearOutgoing();

        using var publisher = new MediaLinkStatePublisher(coordinator, hub);
        await publisher.PublishSnapshotAsync(session);
        await Task.Delay(100);
        Assert.Contains(socket.Outgoing, j => j.Contains("platform"));
        Assert.DoesNotContain(socket.Outgoing, j => j.Contains("\"title\":\"ext\"") || j.Contains("\"Title\":\"ext\""));
        Assert.DoesNotContain(socket.Outgoing, j => j.Contains("ext", StringComparison.Ordinal));
    }

    private static string Auth(string token) =>
        MediaLinkMessageSerializer.Serialize(MediaLinkMessageSerializer.Create(
            MediaLinkProtocol.TypeAuth,
            new MediaLinkAuthPayload { Token = token }));

    private static string Subscribe(params string[] channels) =>
        MediaLinkMessageSerializer.Serialize(MediaLinkMessageSerializer.Create(
            MediaLinkProtocol.TypeSubscribe,
            new MediaLinkSubscribePayload { Channels = channels.ToList() }));
}
