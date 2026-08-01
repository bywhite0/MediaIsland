using MediaIsland.Models;
using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Lyrics.Models;
using MediaIsland.Services.Media;
using MediaIsland.Services.MediaLink;
using MediaIsland.Services.MediaLink.Protocol;
using Xunit;

namespace MediaIsland.Tests.MediaLink;

public class EffectiveMediaSourceExtensionsTests
{
    [Fact]
    public void GetCurrentUiMediaInfo_WithoutFacade_UsesPlatformMedia()
    {
        var platform = Sample("platform");
        var media = new StubMediaService { CurrentMediaInfo = platform };

        var result = ((IEffectiveMediaSource?)null).GetCurrentUiMediaInfo(media);

        Assert.Same(platform, result);
    }

    [Fact]
    public void GetCurrentUiMediaInfo_ExternalOnlyFacadeWithoutInjection_DoesNotFallBackToPlatformMedia()
    {
        var media = new StubMediaService { CurrentMediaInfo = Sample("platform") };
        var lyrics = new LyricsSearchService([], [], () => new LyricsSourceSettings());
        var store = new MediaLinkInjectionStore();
        var settings = new PluginSettings
        {
            MediaLinkMediaSourceMode = MediaLinkMediaSourceMode.ExternalOnly,
            MediaLinkUiUsesEffective = true
        };
        using var effectiveSource = new MediaSourceCoordinator(media, lyrics, store, () => settings);

        var result = effectiveSource.GetCurrentUiMediaInfo(media);

        Assert.Null(result);
    }

    [Fact]
    public void GetCurrentUiMediaInfo_ExternalOnlyFacadeWithInjection_UsesEffectiveMedia()
    {
        var platform = Sample("platform");
        var media = new StubMediaService { CurrentMediaInfo = platform };
        var lyrics = new LyricsSearchService([], [], () => new LyricsSourceSettings());
        var store = new MediaLinkInjectionStore();
        Assert.True(store.TrySetMedia(new MediaLinkMediaInjectPayload
        {
            Title = "external",
            PlaybackState = "Paused"
        }, out _));
        var settings = new PluginSettings
        {
            MediaLinkMediaSourceMode = MediaLinkMediaSourceMode.ExternalOnly,
            MediaLinkUiUsesEffective = true
        };
        using var effectiveSource = new MediaSourceCoordinator(media, lyrics, store, () => settings);

        var result = effectiveSource.GetCurrentUiMediaInfo(media);

        Assert.Equal("external", result?.Title);
    }

    private static MediaInfo Sample(string title) => new(
        "app",
        title,
        "artist",
        null,
        TimeSpan.Zero,
        TimeSpan.FromMinutes(1),
        new MediaPlaybackInfo(MediaPlaybackState.Playing),
        null,
        null);

    private sealed class StubMediaService : IMediaService
    {
        public event EventHandler<MediaInfoChangedEventArgs>? MediaInfoChanged
        {
            add { }
            remove { }
        }

        public MediaInfo? CurrentMediaInfo { get; init; }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task EnsureStartedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
