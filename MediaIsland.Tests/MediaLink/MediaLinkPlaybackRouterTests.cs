using MediaIsland.Models;
using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Lyrics.Models;
using MediaIsland.Services.Media;
using MediaIsland.Services.Media.Platform;
using MediaIsland.Services.MediaLink;
using MediaIsland.Services.MediaLink.Protocol;
using Xunit;

namespace MediaIsland.Tests.MediaLink;

public class MediaLinkPlaybackRouterTests
{
    [Theory]
    [InlineData(MediaPlaybackCommandStatus.Succeeded, "ok")]
    [InlineData(MediaPlaybackCommandStatus.NotSupported, "not_supported")]
    [InlineData(MediaPlaybackCommandStatus.NoSession, "no_session")]
    [InlineData(MediaPlaybackCommandStatus.Failed, "internal")]
    public async Task PlatformCommand_MapsControllerStatus(
        MediaPlaybackCommandStatus status,
        string expectedCodeOrOk)
    {
        var controller = new StubController(status);
        var media = new FakeMediaService
        {
            CurrentMediaInfo = new MediaInfo(
                "app", "t", "a", null,
                TimeSpan.Zero, TimeSpan.FromMinutes(1),
                new MediaPlaybackInfo(MediaPlaybackState.Playing),
                null, null)
        };
        var store = new MediaLinkInjectionStore();
        var lyrics = new LyricsSearchService([], [], () => new LyricsSourceSettings());
        var settings = new PluginSettings
        {
            MediaLinkMediaSourceMode = MediaLinkMediaSourceMode.PlatformOnly
        };
        using var coordinator = new MediaSourceCoordinator(media, lyrics, store, () => settings);
        coordinator.Recompute();

        var (session, socket) = await MediaLinkSessionPhase2Tests.AuthedSession(
            store, coordinator, () => controller);
        socket.ClearOutgoing();

        await session.HandleMessageAsync(
            MediaLinkMessageSerializer.Serialize(MediaLinkMessageSerializer.Create(
                MediaLinkProtocol.TypePlaybackCommand,
                new MediaLinkPlaybackCommandPayload { Action = "play" },
                id: "p1")),
            CancellationToken.None);

        if (expectedCodeOrOk == "ok")
        {
            Assert.Contains(socket.Outgoing, j =>
                j.Contains("\"type\":\"ok\"", StringComparison.Ordinal) ||
                j.Contains(MediaLinkProtocol.TypeOk, StringComparison.Ordinal));
        }
        else
        {
            Assert.Contains(socket.Outgoing, j => j.Contains(expectedCodeOrOk, StringComparison.Ordinal));
        }
    }

    private sealed class StubController(MediaPlaybackCommandStatus status) : IMediaPlaybackController
    {
        public MediaPlaybackCapabilities Capabilities =>
            MediaPlaybackCapabilities.Play |
            MediaPlaybackCapabilities.Pause |
            MediaPlaybackCapabilities.Next |
            MediaPlaybackCapabilities.Previous;

        public Task<MediaPlaybackCommandResult> ExecuteAsync(
            MediaPlaybackCommand command,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MediaPlaybackCommandResult(status, status == MediaPlaybackCommandStatus.Failed ? "boom" : null));
    }
}
