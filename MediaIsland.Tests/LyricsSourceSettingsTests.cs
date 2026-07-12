using Xunit;
using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Lyrics.Models;
using MediaIsland.Services.Media;

namespace MediaIsland.Tests;

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
}
