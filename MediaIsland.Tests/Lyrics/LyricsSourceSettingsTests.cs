using System.Text.Json;
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
        Assert.DoesNotContain(normalized.Sources, source => source.Id == LyricsSourceId.SPlayerNext);
        Assert.Equal("https://example.com/api", normalized.AmllApiBaseUrl);
        Assert.Equal(LyricsSourceSettings.DefaultSPlayerNextApiBaseUrl, normalized.SPlayerNextApiBaseUrl);
    }

    [Fact]
    public void NormalizeAmllBaseUrl_RejectsInvalid()
    {
        Assert.Equal(string.Empty, LyricsSourceSettings.NormalizeAmllBaseUrl("not-a-url"));
        Assert.Equal(string.Empty, LyricsSourceSettings.NormalizeAmllBaseUrl("ftp://x"));
        Assert.Equal("https://host", LyricsSourceSettings.NormalizeAmllBaseUrl("https://host/"));
    }

    [Fact]
    public void NormalizeSPlayerNextBaseUrl_DefaultsAndStripsApiPath()
    {
        Assert.Equal(
            LyricsSourceSettings.DefaultSPlayerNextApiBaseUrl,
            LyricsSourceSettings.NormalizeSPlayerNextBaseUrl(""));
        Assert.Equal(
            "http://127.0.0.1:14558",
            LyricsSourceSettings.NormalizeSPlayerNextBaseUrl("http://127.0.0.1:14558/api"));
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

    [Fact]
    public void Defaults_StripCreditsOn_MaskOff_ListsEmpty()
    {
        var settings = new LyricsSourceSettings();

        Assert.True(settings.StripCreditLines);
        Assert.False(settings.MaskEnabled);
        Assert.Empty(settings.CreditKeywords);
        Assert.Empty(settings.CreditRegexes);
        Assert.Empty(settings.MaskWords);
        Assert.Empty(settings.MaskRegexes);
    }

    [Fact]
    public void Clone_CopiesCleanupFields_IntoIndependentLists()
    {
        var settings = new LyricsSourceSettings
        {
            StripCreditLines = false,
            CreditKeywords = ["监修"],
            CreditRegexes = ["^OP"],
            MaskEnabled = true,
            MaskWords = ["坏词"],
            MaskRegexes = ["b[a]d"]
        };

        var clone = settings.Clone();
        settings.CreditKeywords.Add("后加");

        Assert.False(clone.StripCreditLines);
        Assert.Equal(["监修"], clone.CreditKeywords);
        Assert.Equal(["^OP"], clone.CreditRegexes);
        Assert.True(clone.MaskEnabled);
        Assert.Equal(["坏词"], clone.MaskWords);
        Assert.Equal(["b[a]d"], clone.MaskRegexes);
    }

    [Fact]
    public void Normalize_TrimsDropsEmptyAndDeduplicatesRuleLists_KeepingOrder()
    {
        var settings = new LyricsSourceSettings
        {
            CreditKeywords = [" 监修 ", "", "   ", "监修", "出品"],
            CreditRegexes = null!,
            MaskWords = ["b", "a", "b"],
            MaskRegexes = [" x "]
        };

        var normalized = LyricsSourceSettings.Normalize(settings);

        Assert.Equal(["监修", "出品"], normalized.CreditKeywords);
        Assert.Empty(normalized.CreditRegexes);
        Assert.Equal(["b", "a"], normalized.MaskWords);
        Assert.Equal(["x"], normalized.MaskRegexes);
    }

    [Fact]
    public void Deserialize_LegacyJsonWithoutCleanupFields_UsesDefaults()
    {
        var restored = JsonSerializer.Deserialize<LyricsSourceSettings>("""{"AmllApiBaseUrl":""}""")!;
        var normalized = LyricsSourceSettings.Normalize(restored);

        Assert.True(normalized.StripCreditLines);
        Assert.False(normalized.MaskEnabled);
        Assert.Empty(normalized.CreditKeywords);
        Assert.Empty(normalized.MaskWords);
    }

    [Fact]
    public void Serialize_RoundTripsCleanupFields()
    {
        var settings = new LyricsSourceSettings { StripCreditLines = false, MaskEnabled = true, MaskWords = ["坏词"] };

        var restored = JsonSerializer.Deserialize<LyricsSourceSettings>(JsonSerializer.Serialize(settings))!;

        Assert.False(restored.StripCreditLines);
        Assert.True(restored.MaskEnabled);
        Assert.Equal(["坏词"], restored.MaskWords);
    }
}
