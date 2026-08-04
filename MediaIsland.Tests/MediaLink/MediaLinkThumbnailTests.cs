using System.Text.Json;
using System.Net.WebSockets;
using MediaIsland.Models;
using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Lyrics.Models;
using MediaIsland.Services.Media;
using MediaIsland.Services.MediaLink;
using MediaIsland.Services.MediaLink.Mapping;
using MediaIsland.Services.MediaLink.Protocol;
using Xunit;

namespace MediaIsland.Tests.MediaLink;

public class MediaLinkThumbnailTests
{
    [Fact]
    public async Task Unauthenticated_ThumbnailGet_UnauthorizedAndCloses()
    {
        var socket = new FakeMediaLinkSocket();
        var session = new MediaLinkSession(socket, new MediaLinkSessionOptions { ExpectedToken = "secret" });

        await session.HandleMessageAsync(
            MediaLinkMessageSerializer.Serialize(MediaLinkMessageSerializer.Create(
                MediaLinkProtocol.TypeThumbnailGet, id: "t1")),
            CancellationToken.None);

        Assert.Contains(socket.Outgoing, j => j.Contains(MediaLinkProtocol.ErrorUnauthorized));
        Assert.Equal(WebSocketState.Closed, socket.State);
    }

    [Fact]
    public async Task ThumbnailGet_WithoutCoordinator_ReturnsInternal()
    {
        var (session, socket) = await MediaLinkSessionPhase2Tests.AuthedSession();
        socket.ClearOutgoing();

        await session.HandleMessageAsync(
            MediaLinkMessageSerializer.Serialize(MediaLinkMessageSerializer.Create(
                MediaLinkProtocol.TypeThumbnailGet, id: "t1")),
            CancellationToken.None);

        Assert.Contains(socket.Outgoing, j => j.Contains(MediaLinkProtocol.ErrorInternal));
    }

    [Fact]
    public async Task ThumbnailGet_NoMedia_ReturnsNoSession()
    {
        var (session, socket) = await MediaLinkSessionPhase2Tests.AuthedSession(
            coordinator: CreateCoordinator(out _));
        socket.ClearOutgoing();

        await session.HandleMessageAsync(
            MediaLinkMessageSerializer.Serialize(MediaLinkMessageSerializer.Create(
                MediaLinkProtocol.TypeThumbnailGet, id: "t1")),
            CancellationToken.None);

        Assert.Contains(socket.Outgoing, j => j.Contains(MediaLinkProtocol.ErrorNoSession));
    }

    [Fact]
    public async Task ThumbnailGet_MediaWithoutThumbnail_ReturnsEmptyPayloadNotError()
    {
        // 无封面不是错误：客户端按"当前无可用封面"处理，仍需拿到 trackToken。
        var coordinator = CreateCoordinator(out var media);
        media.Raise(CreateMedia("app", "Song", "Artist"), MediaInfoChangeKind.MediaProperties);
        var (session, socket) = await MediaLinkSessionPhase2Tests.AuthedSession(coordinator: coordinator);
        socket.ClearOutgoing();

        await session.HandleMessageAsync(
            MediaLinkMessageSerializer.Serialize(MediaLinkMessageSerializer.Create(
                MediaLinkProtocol.TypeThumbnailGet, id: "t1")),
            CancellationToken.None);

        var payload = SingleThumbnailPayload(socket);
        Assert.Null(payload.DataBase64);
        Assert.Null(payload.MimeType);
        Assert.NotNull(payload.TrackToken);
    }

    [Fact]
    public async Task ThumbnailGet_StaleTrackToken_ReturnsCurrentTokenWithoutData()
    {
        // 请求上一首的封面时不得回落到当前曲目的图，否则客户端会配错封面。
        var coordinator = CreateCoordinator(out var media);
        media.Raise(CreateMedia("app", "New Song", "Artist"), MediaInfoChangeKind.MediaProperties);
        var (session, socket) = await MediaLinkSessionPhase2Tests.AuthedSession(coordinator: coordinator);
        socket.ClearOutgoing();

        await session.HandleMessageAsync(
            MediaLinkMessageSerializer.Serialize(MediaLinkMessageSerializer.Create(
                MediaLinkProtocol.TypeThumbnailGet,
                new MediaLinkThumbnailGetPayload { TrackToken = "stale-token" },
                id: "t1")),
            CancellationToken.None);

        var payload = SingleThumbnailPayload(socket);
        Assert.Null(payload.DataBase64);
        var expected = MediaLinkDtoMapper.ComputeTrackToken("app", "New Song", "Artist", null);
        Assert.Equal(expected, payload.TrackToken);
    }

    [Fact]
    public async Task ThumbnailGet_EchoesRequestId()
    {
        var coordinator = CreateCoordinator(out var media);
        media.Raise(CreateMedia("app", "Song", "Artist"), MediaInfoChangeKind.MediaProperties);
        var (session, socket) = await MediaLinkSessionPhase2Tests.AuthedSession(coordinator: coordinator);
        socket.ClearOutgoing();

        await session.HandleMessageAsync(
            MediaLinkMessageSerializer.Serialize(MediaLinkMessageSerializer.Create(
                MediaLinkProtocol.TypeThumbnailGet, id: "req-42")),
            CancellationToken.None);

        var message = MediaLinkMessageSerializer.Deserialize(Assert.Single(socket.Outgoing))!;
        Assert.Equal(MediaLinkProtocol.TypeThumbnail, message.Type);
        Assert.Equal("req-42", message.Id);
    }

    [Fact]
    public async Task ThumbnailGet_TrackTokenMatchesMediaUpdated()
    {
        // WS 与 media.updated 必须给出同一个 trackToken，否则客户端无法判断封面归属。
        var coordinator = CreateCoordinator(out var media);
        var info = CreateMedia("player", "Song", "Artist");
        media.Raise(info, MediaInfoChangeKind.MediaProperties);
        var (session, socket) = await MediaLinkSessionPhase2Tests.AuthedSession(coordinator: coordinator);
        socket.ClearOutgoing();

        await session.HandleMessageAsync(
            MediaLinkMessageSerializer.Serialize(MediaLinkMessageSerializer.Create(
                MediaLinkProtocol.TypeThumbnailGet, id: "t1")),
            CancellationToken.None);

        var payload = SingleThumbnailPayload(socket);
        var mediaDto = MediaLinkDtoMapper.ToMediaDto(info, MediaInfoChangeKind.MediaProperties)!;
        Assert.Equal(mediaDto.TrackToken, payload.TrackToken);
    }

    [Fact]
    public async Task EncodePngAsync_NullMedia_ReturnsNull()
    {
        Assert.Null(await MediaLinkThumbnail.EncodePngAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task EncodePngAsync_NoThumbnail_ReturnsNull()
    {
        var media = CreateMedia("app", "Song", "Artist");
        Assert.Null(await MediaLinkThumbnail.EncodePngAsync(media, CancellationToken.None));
    }

    [Fact]
    public async Task EncodePngAsync_ThumbnailSourceReturningNull_ReturnsNull()
    {
        var media = CreateMedia("app", "Song", "Artist") with
        {
            ThumbnailSource = new MediaThumbnail((_, _) =>
                Task.FromResult<Avalonia.Media.Imaging.Bitmap?>(null))
        };

        Assert.Null(await MediaLinkThumbnail.EncodePngAsync(media, CancellationToken.None));
    }

    private static MediaLinkThumbnailPayload SingleThumbnailPayload(FakeMediaLinkSocket socket)
    {
        var message = MediaLinkMessageSerializer.Deserialize(Assert.Single(socket.Outgoing))!;
        Assert.Equal(MediaLinkProtocol.TypeThumbnail, message.Type);
        var payload = MediaLinkMessageSerializer.DeserializePayload<MediaLinkThumbnailPayload>(message.Payload);
        Assert.NotNull(payload);
        return payload;
    }

    private static MediaInfo CreateMedia(string sourceApp, string title, string artist) =>
        new(sourceApp, title, artist, null, TimeSpan.Zero, TimeSpan.FromMinutes(3),
            new MediaPlaybackInfo(MediaPlaybackState.Playing), null, null);

    private static MediaSourceCoordinator CreateCoordinator(out ThumbnailFakeMediaService media)
    {
        media = new ThumbnailFakeMediaService();
        var lyrics = new LyricsSearchService([], [], () => new LyricsSourceSettings());
        var settings = new PluginSettings { MediaLinkPushUsesEffective = true };
        return new MediaSourceCoordinator(media, lyrics, new MediaLinkInjectionStore(), () => settings);
    }

    internal sealed class ThumbnailFakeMediaService : IMediaService
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
