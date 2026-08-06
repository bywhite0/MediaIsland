using MediaIsland.Services.Lyrics.Models;
using MediaIsland.Services.MediaLink.Mapping;
using Xunit;

namespace MediaIsland.Tests.Lyrics;

public class LocalFileSourceIdTests
{
    /// <summary>
    /// 该枚举会被序列化进 Settings.json，序列化配置来自 ClassIsland.Core、不在本仓库控制内。
    /// 若按数值序列化，插在中间会静默改变既有用户配置的语义，故必须追加在末尾。
    /// </summary>
    [Fact]
    public void LocalFile_IsAppendedAtTheEnd()
    {
        Assert.Equal(0, (int)LyricsSourceId.Netease);
        Assert.Equal(1, (int)LyricsSourceId.QqMusic);
        Assert.Equal(2, (int)LyricsSourceId.Kugou);
        Assert.Equal(3, (int)LyricsSourceId.AmllTtml);
        Assert.Equal(4, (int)LyricsSourceId.SPlayerNext);
        Assert.Equal(5, (int)LyricsSourceId.External);
        Assert.Equal(6, (int)LyricsSourceId.LocalFile);
    }

    /// <summary>LocalFile 不是可排序搜索源，不应出现在设置页的歌词源列表里。</summary>
    [Fact]
    public void Normalize_DropsLocalFileEntries()
    {
        var settings = new LyricsSourceSettings
        {
            Sources =
            [
                new LyricsSourceEntry { Id = LyricsSourceId.LocalFile, IsEnabled = true },
                new LyricsSourceEntry { Id = LyricsSourceId.Netease, IsEnabled = true }
            ]
        };

        var normalized = LyricsSourceSettings.Normalize(settings);

        Assert.DoesNotContain(normalized.Sources, source => source.Id == LyricsSourceId.LocalFile);
        Assert.Contains(normalized.Sources, source => source.Id == LyricsSourceId.Netease);
    }

    /// <summary>含 LocalFile 的既有配置反序列化后，其余来源行为不变。</summary>
    [Fact]
    public void Normalize_KeepsOtherSourcesIntact_WhenLocalFilePresent()
    {
        var settings = new LyricsSourceSettings
        {
            Sources =
            [
                new LyricsSourceEntry { Id = LyricsSourceId.LocalFile, IsEnabled = true },
                new LyricsSourceEntry { Id = LyricsSourceId.Kugou, IsEnabled = false, UseWordSyncedLyrics = false }
            ]
        };

        var normalized = LyricsSourceSettings.Normalize(settings);
        var kugou = Assert.Single(normalized.Sources, source => source.Id == LyricsSourceId.Kugou);

        Assert.False(kugou.IsEnabled);
        Assert.False(kugou.UseWordSyncedLyrics);
    }

    [Fact]
    public void MapLyricsSource_LocalFile_DoesNotDegradeToUnknown()
    {
        Assert.Equal("LocalFile", MediaLinkDtoMapper.MapLyricsSource(LyricsSourceId.LocalFile));
    }
}
