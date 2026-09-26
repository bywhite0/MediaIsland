using System.Text.RegularExpressions;
using MediaIsland.Services.Lyrics.Cleanup;
using MediaIsland.Services.Lyrics.Models;
using Xunit;

namespace MediaIsland.Tests.Lyrics;

public class LyricsCleanupOptionsTests
{
    [Fact]
    public void BuiltInRules_MatchLyricKitCounts_AndAllPatternsCompile()
    {
        Assert.Equal(794, LyricsCreditRules.Keywords.Length);
        Assert.Equal(6, LyricsCreditRules.Patterns.Length);
        Assert.Equal(6, LyricsCleanupOptions.DefaultCreditRegexes.Count);
        Assert.Contains("作词", LyricsCleanupOptions.DefaultCreditKeywords);
    }

    [Fact]
    public void From_DefaultSettings_StripOnMaskOff_UsesBuiltInRules()
    {
        var options = LyricsCleanupOptions.From(LyricsSourceSettings.Normalize(new LyricsSourceSettings()));

        Assert.True(options.StripCredits);
        Assert.False(options.MaskEnabled);
        Assert.Same(LyricsCleanupOptions.DefaultCreditKeywords, options.CreditKeywords);
        Assert.Equal(6, options.CreditRegexes.Count);
        Assert.Empty(options.MaskWords);
        Assert.Empty(options.MaskRegexes);
        Assert.Null(options.Title);
    }

    [Fact]
    public void From_UserKeywords_AreNormalizedAndMergedWithBuiltIns()
    {
        var options = LyricsCleanupOptions.From(new LyricsSourceSettings { CreditKeywords = [" Mixer By "] });

        Assert.Contains("mixerby", options.CreditKeywords);
        Assert.Contains("作词", options.CreditKeywords);
    }

    [Fact]
    public void From_InvalidUserRegex_IsSkipped_ValidOneIsAppended()
    {
        var options = LyricsCleanupOptions.From(new LyricsSourceSettings
        {
            CreditRegexes = ["(", "^OP"],
            MaskEnabled = true,
            MaskRegexes = ["[", "b[a]d"]
        });

        Assert.Equal(7, options.CreditRegexes.Count);
        Assert.Equal("^OP", options.CreditRegexes[^1].ToString());
        Assert.Single(options.MaskRegexes);
    }

    [Fact]
    public void From_MaskWords_AreTrimmedAndBlankDropped()
    {
        var options = LyricsCleanupOptions.From(new LyricsSourceSettings { MaskEnabled = true, MaskWords = [" 坏词 ", " "] });

        Assert.Equal(["坏词"], options.MaskWords);
    }

    [Theory]
    [InlineData("a*")]
    [InlineData(".*")]
    [InlineData("^")]
    [InlineData("")]
    [InlineData("   ")]
    public void TryCompile_RejectsPatternsMatchingEmptyString(string pattern)
    {
        Assert.Null(LyricsCleanupOptions.TryCompile(pattern));
    }

    [Fact]
    public void TryCompile_IsCaseInsensitive_WithTimeout()
    {
        var regex = LyricsCleanupOptions.TryCompile("producer");

        Assert.NotNull(regex);
        Assert.Matches(regex!, "PRODUCER");
        Assert.Equal(LyricsCleanupOptions.RegexTimeout, regex!.MatchTimeout);
    }

    [Fact]
    public void NormalizeKeyword_AppliesNfkcLowercaseAndRemovesWhitespace()
    {
        Assert.Equal("lyricsby", LyricsCleanupOptions.NormalizeKeyword("Ｌｙｒｉｃｓ　Ｂｙ"));
        Assert.Equal("作词", LyricsCleanupOptions.NormalizeKeyword(" 作 词 "));
        Assert.Equal(string.Empty, LyricsCleanupOptions.NormalizeKeyword(null));
    }

    [Fact]
    public void NormalizeKeyword_LoneSurrogate_DoesNotThrow()
    {
        var result = LyricsCleanupOptions.NormalizeKeyword("\uD800作 词");

        Assert.Equal("\uD800作词", result);
    }

    [Fact]
    public void WithMetadata_SplitsArtistsOnCommonSeparators()
    {
        var options = new LyricsCleanupOptions().WithMetadata(new LyricsMetadata(" Song ", "A / B、C，D & E", null, null));

        Assert.Equal("Song", options.Title);
        Assert.Equal(["A", "B", "C", "D", "E"], options.Artists);
    }

    [Fact]
    public void WithMetadata_Null_SetsEmptyTitleSoItIsNotFilledAgain()
    {
        var options = new LyricsCleanupOptions().WithMetadata(null);

        Assert.Equal(string.Empty, options.Title);
        Assert.Empty(options.Artists);
    }

    [Fact]
    public void WithMetadataIfMissing_KeepsExplicitTitle()
    {
        var explicitOptions = new LyricsCleanupOptions { Title = "显式", Artists = ["歌手"] };

        var result = explicitOptions.WithMetadataIfMissing(new LyricsMetadata("元数据", "别人", null, null));

        Assert.Equal("显式", result.Title);
        Assert.Equal(["歌手"], result.Artists);
    }
}
