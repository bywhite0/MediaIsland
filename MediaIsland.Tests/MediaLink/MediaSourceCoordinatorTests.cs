using System.Reflection;
using MediaIsland.Models;
using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Lyrics.Models;
using MediaIsland.Services.Media;
using MediaIsland.Services.MediaLink;
using MediaIsland.Services.MediaLink.Protocol;
using Xunit;

namespace MediaIsland.Tests.MediaLink;

public class MediaSourceCoordinatorTests
{
    private static PluginSettings Settings(
        MediaLinkMediaSourceMode mode = MediaLinkMediaSourceMode.PlatformOnly,
        bool ui = true,
        bool push = true) => new()
    {
        MediaLinkMediaSourceMode = mode,
        MediaLinkUiUsesEffective = ui,
        MediaLinkPushUsesEffective = push
    };

    [Fact]
    public void PlatformOnly_IgnoresInject()
    {
        var media = new FakeMediaService
        {
            CurrentMediaInfo = Sample("platform")
        };
        var lyrics = new LyricsSearchService([], [], () => new LyricsSourceSettings());
        var store = new MediaLinkInjectionStore();
        store.TrySetMedia(new MediaLinkMediaInjectPayload
        {
            Title = "ext", PlaybackState = "Paused"
        }, out _);

        using var c = new MediaSourceCoordinator(media, lyrics, store, () => Settings());
        c.Recompute();
        Assert.Equal("platform", c.EffectiveMediaInfo!.Title);
        Assert.False(c.IsExternalMediaEffective);
    }

    [Fact]
    public void ExternalOnly_UsesInject_OrNull()
    {
        var media = new FakeMediaService { CurrentMediaInfo = Sample("platform") };
        var lyrics = new LyricsSearchService([], [], () => new LyricsSourceSettings());
        var store = new MediaLinkInjectionStore();
        using var c = new MediaSourceCoordinator(
            media, lyrics, store, () => Settings(MediaLinkMediaSourceMode.ExternalOnly));
        c.Recompute();
        Assert.Null(c.EffectiveMediaInfo);

        store.TrySetMedia(new MediaLinkMediaInjectPayload
        {
            Title = "ext", PlaybackState = "Playing"
        }, out _);
        c.Recompute();
        Assert.Equal("ext", c.EffectiveMediaInfo!.Title);
        Assert.True(c.IsExternalMediaEffective);
    }

    [Fact]
    public void ExternalPreferred_FallsBackToPlatform()
    {
        var media = new FakeMediaService { CurrentMediaInfo = Sample("platform") };
        var lyrics = new LyricsSearchService([], [], () => new LyricsSourceSettings());
        var store = new MediaLinkInjectionStore();
        using var c = new MediaSourceCoordinator(
            media, lyrics, store, () => Settings(MediaLinkMediaSourceMode.ExternalPreferred));
        c.Recompute();
        Assert.Equal("platform", c.EffectiveMediaInfo!.Title);
        Assert.False(c.IsExternalMediaEffective);
    }

    [Fact]
    public void Lyrics_ExternalPreferred_DoesNotPairMismatchedPlatformLyrics()
    {
        var platformMedia = Sample("P");
        var media = new FakeMediaService { CurrentMediaInfo = platformMedia };
        var lyrics = new LyricsSearchService([], [], () => new LyricsSourceSettings());
        SeedPlatformLyrics(lyrics, platformMedia, CreateLyrics("platform-lyrics", "P", "a"));

        var store = new MediaLinkInjectionStore();
        Assert.True(store.TrySetMedia(new MediaLinkMediaInjectPayload
        {
            Title = "E",
            Artist = "ext-artist",
            PlaybackState = "Playing"
        }, out _));
        Assert.True(store.TrySetLyrics(new MediaLinkLyricsDto
        {
            Title = "E",
            Artist = "ext-artist",
            Source = "External",
            Document = new MediaLinkLyricsDocumentDto { Lines = [] }
        }, out _));

        using var c = new MediaSourceCoordinator(
            media, lyrics, store, () => Settings(MediaLinkMediaSourceMode.ExternalPreferred));
        c.Recompute();

        Assert.Equal("E", c.EffectiveMediaInfo!.Title);
        Assert.True(c.IsExternalMediaEffective);
        Assert.NotNull(c.EffectiveLyrics);
        Assert.Equal("E", c.EffectiveLyrics!.Title);
        Assert.Equal(LyricsSourceId.External, c.EffectiveLyrics.Source);

        Assert.True(store.TryClear(["lyrics"], out _));
        c.Recompute();

        Assert.Equal("E", c.EffectiveMediaInfo!.Title);
        Assert.Null(c.EffectiveLyrics);
        Assert.NotNull(lyrics.GetCurrentResultFor(platformMedia));
        Assert.Null(lyrics.GetCurrentResultFor(c.ComposeMedia()));
    }

    [Fact]
    public void SettingsModeChange_RaisesEffectiveMediaChanged()
    {
        var media = new FakeMediaService { CurrentMediaInfo = Sample("platform") };
        var lyrics = new LyricsSearchService([], [], () => new LyricsSourceSettings());
        var store = new MediaLinkInjectionStore();
        store.TrySetMedia(new MediaLinkMediaInjectPayload
        {
            Title = "ext", PlaybackState = "Paused"
        }, out _);
        var settings = Settings(MediaLinkMediaSourceMode.PlatformOnly);
        using var c = new MediaSourceCoordinator(media, lyrics, store, () => settings);
        c.Recompute();
        var hits = 0;
        c.EffectiveMediaChanged += (_, _) => hits++;
        settings.MediaLinkMediaSourceMode = MediaLinkMediaSourceMode.ExternalOnly;
        c.Recompute();
        Assert.Equal("ext", c.EffectiveMediaInfo!.Title);
        Assert.True(hits >= 1);
    }

    [Fact]
    public void UiAndPushFlags_SelectComposeOrPlatform()
    {
        var media = new FakeMediaService { CurrentMediaInfo = Sample("platform") };
        var lyrics = new LyricsSearchService([], [], () => new LyricsSourceSettings());
        var store = new MediaLinkInjectionStore();
        store.TrySetMedia(new MediaLinkMediaInjectPayload
        {
            Title = "ext", PlaybackState = "Playing"
        }, out _);

        var settings = Settings(MediaLinkMediaSourceMode.ExternalOnly, ui: false, push: true);
        using var c = new MediaSourceCoordinator(media, lyrics, store, () => settings);
        c.Recompute();

        Assert.Equal("ext", c.ComposeMedia()!.Title);
        Assert.True(c.IsExternalMediaEffective);
        Assert.Equal("platform", c.GetMediaForUi()!.Title);
        Assert.Equal("platform", c.EffectiveMediaInfo!.Title);
        Assert.Equal("ext", c.GetMediaForPush()!.Title);
    }

    [Fact]
    public void PushRawView_TracksPlatformMediaAndLyrics_WhenUiStaysExternal()
    {
        var platformMedia = Sample("platform");
        var media = new FakeMediaService { CurrentMediaInfo = platformMedia };
        var lyrics = new LyricsSearchService([], [], () => new LyricsSourceSettings());
        SeedPlatformLyrics(
            lyrics,
            platformMedia,
            CreateLyrics("platform-lyrics-1", "platform", "a"));

        var store = new MediaLinkInjectionStore();
        Assert.True(store.TrySetMedia(new MediaLinkMediaInjectPayload
        {
            Title = "ext",
            Artist = "ext-artist",
            PlaybackState = "Playing"
        }, out _));
        Assert.True(store.TrySetLyrics(new MediaLinkLyricsDto
        {
            Title = "ext",
            Artist = "ext-artist",
            Source = "External",
            Document = new MediaLinkLyricsDocumentDto { Lines = [] }
        }, out _));

        var settings = Settings(MediaLinkMediaSourceMode.ExternalOnly, ui: true, push: false);
        using var c = new MediaSourceCoordinator(media, lyrics, store, () => settings);
        var mediaEvents = new List<MediaInfoChangedEventArgs>();
        var lyricsEvents = new List<LyricsSearchResultChangedEventArgs>();
        c.EffectiveMediaChanged += (_, e) => mediaEvents.Add(e);
        c.EffectiveLyricsChanged += (_, e) => lyricsEvents.Add(e);

        media.Raise(
            platformMedia with { PlaybackInfo = new MediaPlaybackInfo(MediaPlaybackState.Paused) },
            MediaInfoChangeKind.Playback);

        var mediaEvent = Assert.Single(mediaEvents);
        Assert.Equal(MediaInfoChangeKind.Playback, mediaEvent.ChangeKind);
        Assert.Equal("ext", c.EffectiveMediaInfo!.Title);
        Assert.Equal(MediaPlaybackState.Paused, c.GetMediaForPush()!.PlaybackInfo.PlaybackState);
        Assert.Empty(lyricsEvents);

        PublishPlatformLyrics(
            lyrics,
            platformMedia,
            CreateLyrics("platform-lyrics-2", "platform", "a"));

        Assert.Single(lyricsEvents);
        Assert.Equal("ext", c.EffectiveLyrics!.Title);
        Assert.Equal("platform-lyrics-2", c.GetLyricsForPush()!.Id);
    }

    [Fact]
    public void PushUsesEffectiveChange_RaisesEventsForTheNewPushSnapshots()
    {
        var platformMedia = Sample("platform");
        var media = new FakeMediaService { CurrentMediaInfo = platformMedia };
        var lyrics = new LyricsSearchService([], [], () => new LyricsSourceSettings());
        SeedPlatformLyrics(
            lyrics,
            platformMedia,
            CreateLyrics("platform-lyrics", "platform", "a"));

        var store = new MediaLinkInjectionStore();
        Assert.True(store.TrySetMedia(new MediaLinkMediaInjectPayload
        {
            Title = "ext",
            Artist = "ext-artist",
            PlaybackState = "Playing"
        }, out _));
        Assert.True(store.TrySetLyrics(new MediaLinkLyricsDto
        {
            Title = "ext",
            Artist = "ext-artist",
            Source = "External",
            Document = new MediaLinkLyricsDocumentDto { Lines = [] }
        }, out _));

        var settings = Settings(MediaLinkMediaSourceMode.ExternalOnly, ui: true, push: false);
        using var c = new MediaSourceCoordinator(media, lyrics, store, () => settings);
        var mediaEvents = 0;
        var lyricsEvents = 0;
        c.EffectiveMediaChanged += (_, _) => mediaEvents++;
        c.EffectiveLyricsChanged += (_, _) => lyricsEvents++;

        settings.MediaLinkPushUsesEffective = true;

        Assert.Equal(1, mediaEvents);
        Assert.Equal(1, lyricsEvents);
        Assert.Equal("ext", c.GetMediaForPush()!.Title);
        Assert.Equal(LyricsSourceId.External, c.GetLyricsForPush()!.Source);

        settings.MediaLinkPushUsesEffective = false;

        Assert.Equal(2, mediaEvents);
        Assert.Equal(2, lyricsEvents);
        Assert.Equal("platform", c.GetMediaForPush()!.Title);
        Assert.Equal("platform-lyrics", c.GetLyricsForPush()!.Id);
    }

    private static MediaInfo Sample(string title) => new(
        "app", title, "a", null,
        TimeSpan.Zero, TimeSpan.FromMinutes(3),
        new MediaPlaybackInfo(MediaPlaybackState.Playing),
        null, null);

    private static LyricsSearchResult CreateLyrics(string id, string title, string artist)
    {
        var document = new LyricsDocument(
            new LyricsMetadata(title, artist, null, null),
            [],
            LyricsSyncMode.Unsynced,
            LyricsSourceId.Netease,
            id,
            LyricsFormat.Unknown);
        return new LyricsSearchResult(document, id, title, artist, TimeSpan.Zero, 100, LyricsSourceId.Netease);
    }

    private static void SeedPlatformLyrics(LyricsSearchService service, MediaInfo media, LyricsSearchResult result)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(LyricsSearchService);
        type.GetField("_currentResult", flags)!.SetValue(service, result);
        type.GetField("_currentMediaTitle", flags)!.SetValue(service, media.Title);
        type.GetField("_currentMediaArtist", flags)!.SetValue(service, media.Artist);
    }

    private static void PublishPlatformLyrics(
        LyricsSearchService service,
        MediaInfo media,
        LyricsSearchResult result)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(LyricsSearchService);
        type.GetField("_currentMediaTitle", flags)!.SetValue(service, media.Title);
        type.GetField("_currentMediaArtist", flags)!.SetValue(service, media.Artist);
        var searchVersion = type.GetField("_searchVersion", flags)!.GetValue(service)!;
        type.GetMethod("PublishCurrentResult", flags)!.Invoke(service, [result, searchVersion]);
    }
}
