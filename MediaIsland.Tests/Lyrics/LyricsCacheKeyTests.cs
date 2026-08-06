using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Lyrics.Models;
using MediaIsland.Services.Media;
using Xunit;

namespace MediaIsland.Tests.Lyrics;

public class LyricsCacheKeyTests
{
    private static MediaInfo CreateMedia(
        string sourceApp = "Spotify.exe",
        string title = "Lemon",
        string artist = "米津玄師",
        string album = "Lemon",
        double durationSeconds = 227.4) =>
        new(
            sourceApp,
            title,
            artist,
            album,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(durationSeconds),
            new MediaPlaybackInfo(MediaPlaybackState.Playing),
            null,
            null);

    /// <summary>L2 键是纯曲目标识：换播放器必须命中同一条目。</summary>
    [Fact]
    public void BuildTrackKey_IsIdentical_AcrossPlayers()
    {
        Assert.Equal(
            LyricsSearchService.BuildTrackKey(CreateMedia("QQMusic.exe")),
            LyricsSearchService.BuildTrackKey(CreateMedia("CloudMusic.exe")));
    }

    /// <summary>时长分桶的边界抖动会让同一首歌落到两个键，故键不含时长。</summary>
    [Fact]
    public void BuildTrackKey_IgnoresDurationJitterAcrossBucketBoundary()
    {
        Assert.Equal(
            LyricsSearchService.BuildTrackKey(CreateMedia(durationSeconds: 227.4)),
            LyricsSearchService.BuildTrackKey(CreateMedia(durationSeconds: 227.6)));
    }

    [Fact]
    public void BuildCacheKey_IgnoresDuration()
    {
        Assert.Equal(
            LyricsSearchService.BuildCacheKey(CreateMedia(durationSeconds: 227.4)),
            LyricsSearchService.BuildCacheKey(CreateMedia(durationSeconds: 227.6)));
    }

    /// <summary>L1 键必须保留 allowProviderSearch，否则关闭歌词搜索后 30 分钟内仍会返回歌词。</summary>
    [Fact]
    public void BuildCacheKey_DiffersByAllowProviderSearch()
    {
        Assert.NotEqual(
            LyricsSearchService.BuildCacheKey(CreateMedia(), allowProviderSearch: true),
            LyricsSearchService.BuildCacheKey(CreateMedia(), allowProviderSearch: false));
    }

    /// <summary>SPlayer-Next 走直连分支，其结果不得串给普通播放源。</summary>
    [Fact]
    public void BuildCacheKey_SeparatesSPlayerNextFromOtherPlayers()
    {
        Assert.NotEqual(
            LyricsSearchService.BuildCacheKey(CreateMedia("top.imsyy.splayer-next")),
            LyricsSearchService.BuildCacheKey(CreateMedia("Spotify.exe")));
    }

    /// <summary>除 SPlayer-Next 外，所有播放器共享同一条 L1 条目。</summary>
    [Fact]
    public void BuildCacheKey_IsShared_AmongNonSPlayerNextPlayers()
    {
        Assert.Equal(
            LyricsSearchService.BuildCacheKey(CreateMedia("QQMusic.exe")),
            LyricsSearchService.BuildCacheKey(CreateMedia("CloudMusic.exe")));
    }

    [Fact]
    public void BuildCandidateCacheKey_IsShared_AmongNonSPlayerNextPlayers()
    {
        Assert.Equal(
            LyricsSearchService.BuildCandidateCacheKey(CreateMedia("QQMusic.exe"), LyricsSourceId.Netease),
            LyricsSearchService.BuildCandidateCacheKey(CreateMedia("CloudMusic.exe"), LyricsSourceId.Netease));
    }

    [Fact]
    public void BuildCandidateCacheKey_DiffersByProvider()
    {
        Assert.NotEqual(
            LyricsSearchService.BuildCandidateCacheKey(CreateMedia(), LyricsSourceId.Netease),
            LyricsSearchService.BuildCandidateCacheKey(CreateMedia(), LyricsSourceId.QqMusic));
    }

    [Fact]
    public void BuildCacheKey_DiffersByTrack()
    {
        Assert.NotEqual(
            LyricsSearchService.BuildCacheKey(CreateMedia(title: "Lemon")),
            LyricsSearchService.BuildCacheKey(CreateMedia(title: "Flamingo")));
    }
}
