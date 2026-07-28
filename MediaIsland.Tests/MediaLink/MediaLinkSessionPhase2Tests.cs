using System.Net.WebSockets;
using MediaIsland.Models;
using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Lyrics.Models;
using MediaIsland.Services.Media;
using MediaIsland.Services.Media.Platform;
using MediaIsland.Services.MediaLink;
using MediaIsland.Services.MediaLink.Protocol;
using Xunit;

namespace MediaIsland.Tests.MediaLink;

public class MediaLinkSessionPhase2Tests
{
    [Fact]
    public async Task Unauthenticated_MediaInject_UnauthorizedAndCloses()
    {
        var socket = new FakeMediaLinkSocket();
        var session = new MediaLinkSession(socket, new MediaLinkSessionOptions
        {
            ExpectedToken = "secret",
            InjectionStore = new MediaLinkInjectionStore()
        });

        await session.HandleMessageAsync(
            MediaLinkMessageSerializer.Serialize(MediaLinkMessageSerializer.Create(
                MediaLinkProtocol.TypeMediaInject,
                new MediaLinkMediaInjectPayload { Title = "T", PlaybackState = "Paused" },
                id: "1")),
            CancellationToken.None);

        Assert.Contains(socket.Outgoing, j => j.Contains(MediaLinkProtocol.ErrorUnauthorized));
        Assert.Equal(WebSocketState.Closed, socket.State);
    }

    [Fact]
    public async Task Authenticated_MediaInject_ReturnsOk_And_Stores()
    {
        var store = new MediaLinkInjectionStore();
        var (session, socket) = await AuthedSession(store: store);
        socket.ClearOutgoing();

        await session.HandleMessageAsync(
            MediaLinkMessageSerializer.Serialize(MediaLinkMessageSerializer.Create(
                MediaLinkProtocol.TypeMediaInject,
                new MediaLinkMediaInjectPayload
                {
                    Title = "Injected",
                    PositionMs = 0,
                    DurationMs = 1,
                    PlaybackState = "Paused"
                },
                id: "inj1")),
            CancellationToken.None);

        Assert.Contains(socket.Outgoing, j =>
            j.Contains(MediaLinkProtocol.TypeOk) && j.Contains("inj1"));
        Assert.Equal("Injected", store.GetMediaSnapshot()!.Title);
    }

    [Fact]
    public async Task ExternalNext_ReturnsNotSupported_WithoutClose()
    {
        var store = new MediaLinkInjectionStore();
        store.TrySetMedia(new MediaLinkMediaInjectPayload
        {
            Title = "E",
            PlaybackState = "Playing"
        }, out _);

        var media = new FakeMediaService();
        var lyrics = new LyricsSearchService([], [], () => new LyricsSourceSettings());
        var settings = new PluginSettings
        {
            MediaLinkMediaSourceMode = MediaLinkMediaSourceMode.ExternalOnly
        };
        using var coordinator = new MediaSourceCoordinator(media, lyrics, store, () => settings);
        coordinator.Recompute();

        var (session, socket) = await AuthedSession(store: store, coordinator: coordinator);
        socket.ClearOutgoing();

        await session.HandleMessageAsync(
            MediaLinkMessageSerializer.Serialize(MediaLinkMessageSerializer.Create(
                MediaLinkProtocol.TypePlaybackCommand,
                new MediaLinkPlaybackCommandPayload { Action = "next" },
                id: "n1")),
            CancellationToken.None);

        Assert.Contains(socket.Outgoing, j => j.Contains(MediaLinkProtocol.ErrorNotSupported));
        Assert.Equal(WebSocketState.Open, socket.State);
    }

    [Fact]
    public async Task MissingTitle_Inject_BadRequest_NoClose()
    {
        var store = new MediaLinkInjectionStore();
        var (session, socket) = await AuthedSession(store: store);
        socket.ClearOutgoing();

        await session.HandleMessageAsync(
            MediaLinkMessageSerializer.Serialize(MediaLinkMessageSerializer.Create(
                MediaLinkProtocol.TypeMediaInject,
                new MediaLinkMediaInjectPayload { Title = "", PlaybackState = "Paused" },
                id: "bad")),
            CancellationToken.None);

        Assert.Contains(socket.Outgoing, j => j.Contains(MediaLinkProtocol.ErrorBadRequest));
        Assert.Equal(WebSocketState.Open, socket.State);
    }

    internal static async Task<(MediaLinkSession Session, FakeMediaLinkSocket Socket)> AuthedSession(
        MediaLinkInjectionStore? store = null,
        MediaSourceCoordinator? coordinator = null,
        Func<IMediaPlaybackController?>? playbackControllerAccessor = null)
    {
        var socket = new FakeMediaLinkSocket();
        var session = new MediaLinkSession(socket, new MediaLinkSessionOptions
        {
            ExpectedToken = "secret",
            InjectionStore = store,
            Coordinator = coordinator,
            PlaybackControllerAccessor = playbackControllerAccessor
        });

        await session.HandleMessageAsync(
            MediaLinkMessageSerializer.Serialize(MediaLinkMessageSerializer.Create(
                MediaLinkProtocol.TypeAuth,
                new MediaLinkAuthPayload { Token = "secret" },
                id: "auth")),
            CancellationToken.None);

        return (session, socket);
    }
}
