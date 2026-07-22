using Xunit;
using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Lyrics.Models;
using MediaIsland.Services.Media;
using MediaIsland.SettingsPages;

namespace MediaIsland.Tests.Lyrics;

public class LyricsSourceSettingsTests
{
    [Fact]
    public void Normalize_AddsMissingSources_AndKeepsOrder()
    {
        var settings = new LyricsSourceSettings
        {
            Sources =
            [
                new LyricsSourceEntry { Id = LyricsSourceId.Netease, IsEnabled = true, UseWordSyncedLyrics = false }
            ],
            AmllApiBaseUrl = " https://example.com/api/ "
        };

        var normalized = LyricsSourceSettings.Normalize(settings);
        Assert.Equal(4, normalized.Sources.Count);
        Assert.Equal(LyricsSourceId.Netease, normalized.Sources[0].Id);
        Assert.Contains(normalized.Sources, source => source.Id == LyricsSourceId.QqMusic);
        Assert.Equal("https://example.com/api", normalized.AmllApiBaseUrl);
    }

    [Fact]
    public void NormalizeAmllBaseUrl_RejectsInvalid()
    {
        Assert.Equal(string.Empty, LyricsSourceSettings.NormalizeAmllBaseUrl("not-a-url"));
        Assert.Equal(string.Empty, LyricsSourceSettings.NormalizeAmllBaseUrl("ftp://x"));
        Assert.Equal("https://host", LyricsSourceSettings.NormalizeAmllBaseUrl("https://host/"));
    }

    [Fact]
    public void NormalizeAndClone_PreserveSourceGlobalOffset()
    {
        var settings = new LyricsSourceSettings
        {
            Sources =
            [
                new LyricsSourceEntry
                {
                    Id = LyricsSourceId.QqMusic,
                    GlobalOffsetMilliseconds = 250
                }
            ]
        };

        var normalized = LyricsSourceSettings.Normalize(settings);
        var clone = normalized.Clone();

        Assert.Equal(TimeSpan.FromMilliseconds(250), normalized.GetGlobalOffset(LyricsSourceId.QqMusic));
        Assert.Equal(TimeSpan.FromMilliseconds(250), clone.GetGlobalOffset(LyricsSourceId.QqMusic));
        Assert.Equal(TimeSpan.Zero, normalized.GetGlobalOffset(LyricsSourceId.Netease));
    }

    [Fact]
    public void GlobalOffsetText_EmptyValueDoesNotSaveOrChangeOffset()
    {
        var saveCount = 0;
        var item = new LyricsSourceItemViewModel(
            new LyricsSourceEntry
            {
                Id = LyricsSourceId.QqMusic,
                GlobalOffsetMilliseconds = 250
            },
            () => saveCount++);

        item.GlobalOffsetMillisecondsText = string.Empty;

        Assert.Equal(250, item.GlobalOffsetMilliseconds);
        Assert.Equal(0, saveCount);
    }
}
