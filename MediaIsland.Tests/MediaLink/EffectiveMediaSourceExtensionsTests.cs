using System.Reflection;
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

    // 以下四条是第 8 期 Task 6 的页面层判据：设置页「当前使用」行经
    // GetCurrentUiLyrics 读取，判据全部打在这条读缝上。页面不可在测试中构造
    // （仓库无 Avalonia.Headless），显示串内插值由页面既有分支承担。

    [Fact]
    public void GetCurrentUiLyrics_WithoutFacade_UsesLocalSearchResult()
    {
        var platform = Sample("platform");
        var media = new StubMediaService { CurrentMediaInfo = platform };
        var lyrics = new LyricsSearchService([], [], () => new LyricsSourceSettings());
        var local = CreateLyrics("local-1", "platform", "artist");
        SeedPlatformLyrics(lyrics, platform, local);

        var result = ((IEffectiveMediaSource?)null).GetCurrentUiLyrics(lyrics, media);

        Assert.Same(local, result);
    }

    [Fact]
    public void GetCurrentUiLyrics_InjectionShape_YieldsExternalWithOriginSource()
    {
        // 纯接收端形态：本机媒体为 null，媒体与歌词均来自注入，歌词标注真实来源。
        // 这是用户实报缺陷的判据——旧读法（本机搜索服务）在此形态下只会得到 null。
        var media = new StubMediaService();
        var lyrics = new LyricsSearchService([], [], () => new LyricsSourceSettings());
        var store = new MediaLinkInjectionStore();
        Assert.True(store.TrySetMedia(new MediaLinkMediaInjectPayload
        {
            Title = "E",
            Artist = "ext-artist",
            PlaybackState = "Playing"
        }, out _));
        Assert.True(store.TrySetLyrics(InjectedLyrics("QqMusic"), out _));
        var settings = new PluginSettings
        {
            MediaLinkMediaSourceMode = MediaLinkMediaSourceMode.ExternalPreferred,
            MediaLinkUiUsesEffective = true
        };
        using var effectiveSource = new MediaSourceCoordinator(media, lyrics, store, () => settings);

        var result = effectiveSource.GetCurrentUiLyrics(lyrics, media);

        Assert.Null(media.CurrentMediaInfo);
        Assert.NotNull(result);
        Assert.Equal(LyricsSourceId.External, result!.Source);
        Assert.Equal(LyricsSourceId.QqMusic, result.OriginSource);
    }

    [Fact]
    public void GetCurrentUiLyrics_PlatformShape_KeepsLocalSearchResult()
    {
        // 本机形态回归：协调器在场但无注入时，本机搜索结果照旧到达该行。
        var platform = Sample("platform");
        var media = new StubMediaService { CurrentMediaInfo = platform };
        var lyrics = new LyricsSearchService([], [], () => new LyricsSourceSettings());
        var local = CreateLyrics("local-1", "platform", "artist");
        SeedPlatformLyrics(lyrics, platform, local);
        var store = new MediaLinkInjectionStore();
        var settings = new PluginSettings
        {
            MediaLinkMediaSourceMode = MediaLinkMediaSourceMode.ExternalPreferred,
            MediaLinkUiUsesEffective = true
        };
        using var effectiveSource = new MediaSourceCoordinator(media, lyrics, store, () => settings);

        var result = effectiveSource.GetCurrentUiLyrics(lyrics, media);

        Assert.Same(local, result);
    }

    [Fact]
    public void GetCurrentUiLyrics_UiUsesEffectiveOff_FallsBackToLocalRead()
    {
        // 开关关闭回本机读法：注入在场也不选它，分支在协调器内而非页面。
        var platform = Sample("platform");
        var media = new StubMediaService { CurrentMediaInfo = platform };
        var lyrics = new LyricsSearchService([], [], () => new LyricsSourceSettings());
        var local = CreateLyrics("local-1", "platform", "artist");
        SeedPlatformLyrics(lyrics, platform, local);
        var store = new MediaLinkInjectionStore();
        Assert.True(store.TrySetMedia(new MediaLinkMediaInjectPayload
        {
            Title = "E",
            Artist = "ext-artist",
            PlaybackState = "Playing"
        }, out _));
        Assert.True(store.TrySetLyrics(InjectedLyrics("QqMusic"), out _));
        var settings = new PluginSettings
        {
            MediaLinkMediaSourceMode = MediaLinkMediaSourceMode.ExternalPreferred,
            MediaLinkUiUsesEffective = false
        };
        using var effectiveSource = new MediaSourceCoordinator(media, lyrics, store, () => settings);

        var result = effectiveSource.GetCurrentUiLyrics(lyrics, media);

        Assert.Same(local, result);
        Assert.NotEqual(LyricsSourceId.External, result!.Source);
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

    private static MediaLinkLyricsDto InjectedLyrics(string originSource) => new()
    {
        Id = "ly-1",
        Title = "E",
        Artist = "ext-artist",
        DurationMs = 120_000,
        Document = new MediaLinkLyricsDocumentDto
        {
            Format = "Lrc",
            SyncMode = "Line",
            ProviderItemId = "item-1",
            Source = originSource,
            Lines =
            [
                new MediaLinkLyricsLineDto { StartMs = 0, EndMs = 5_000, Text = "line" }
            ]
        }
    };

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
