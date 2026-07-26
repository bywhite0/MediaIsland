using Avalonia.Media;
using MediaIsland.Models;
using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Lyrics.Models;
using Xunit;

namespace MediaIsland.Tests.Lyrics;

/// <summary>
/// 按展示部分解析歌词字体与字重，未配置时回退到岛屿全局字体。
/// </summary>
public class LyricsTypographyTests
{
    private static readonly FontFamily FallbackFamily = new("Segoe UI");
    private const FontWeight FallbackWeight = FontWeight.Medium;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveFontFamily_FallsBackWhenNotConfigured(string? configured)
    {
        Assert.Equal(FallbackFamily, LyricsTypography.ResolveFontFamily(configured, FallbackFamily));
    }

    [Fact]
    public void ResolveFontFamily_UsesConfiguredNameAndTrimsIt()
    {
        var resolved = LyricsTypography.ResolveFontFamily("  Noto Sans JP  ", FallbackFamily);

        Assert.Equal("Noto Sans JP", resolved.Name);
    }

    [Fact]
    public void ResolveFontWeight_FallsBackWhenFollowingGlobal()
    {
        Assert.Equal(FallbackWeight, LyricsTypography.ResolveFontWeight(0, FallbackWeight));
    }

    [Theory]
    [InlineData(100, FontWeight.Thin)]
    [InlineData(400, FontWeight.Normal)]
    [InlineData(700, FontWeight.Bold)]
    [InlineData(900, FontWeight.Black)]
    public void ResolveFontWeight_UsesConfiguredWeight(int configured, FontWeight expected)
    {
        Assert.Equal(expected, LyricsTypography.ResolveFontWeight(configured, FallbackWeight));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(123)]
    [InlineData(1000)]
    public void ResolveFontWeight_FallsBackOnUnsupportedWeight(int configured)
    {
        Assert.Equal(FallbackWeight, LyricsTypography.ResolveFontWeight(configured, FallbackWeight));
    }

    [Fact]
    public void FontWeightIndex_RoundTrips()
    {
        foreach (var weight in LyricsTypography.FontWeightOptions)
        {
            var index = LyricsTypography.ToFontWeightIndex(weight);
            Assert.Equal(weight, LyricsTypography.FromFontWeightIndex(index));
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(99)]
    public void FromFontWeightIndex_FallsBackToFollowGlobalOnOutOfRange(int index)
    {
        Assert.Equal(0, LyricsTypography.FromFontWeightIndex(index));
    }

    [Fact]
    public void Resolve_UsesPerPartConfiguration()
    {
        var settings = new PluginSettings
        {
            LyricsOriginalFontFamily = "Original Font",
            LyricsOriginalFontWeight = 700,
            LyricsTranslationFontFamily = "Translation Font",
            LyricsTranslationFontWeight = 300,
            LyricsRomanizationFontFamily = "Romanization Font"
        };

        var original = LyricsTypography.Resolve(
            settings, LyricsDisplayPart.Original, FallbackFamily, FallbackWeight);
        var translation = LyricsTypography.Resolve(
            settings, LyricsDisplayPart.Translation, FallbackFamily, FallbackWeight);
        var romanization = LyricsTypography.Resolve(
            settings, LyricsDisplayPart.Romanization, FallbackFamily, FallbackWeight);

        Assert.Equal("Original Font", original.Family.Name);
        Assert.Equal(FontWeight.Bold, original.Weight);
        Assert.Equal("Translation Font", translation.Family.Name);
        Assert.Equal(FontWeight.Light, translation.Weight);
        Assert.Equal("Romanization Font", romanization.Family.Name);
        // 未配置字重的部分继续跟随全局。
        Assert.Equal(FallbackWeight, romanization.Weight);
    }

    [Fact]
    public void Resolve_FallsBackWhenSettingsMissing()
    {
        var typography = LyricsTypography.Resolve(
            null, LyricsDisplayPart.Translation, FallbackFamily, FallbackWeight);

        Assert.Equal(FallbackFamily, typography.Family);
        Assert.Equal(FallbackWeight, typography.Weight);
    }

    [Fact]
    public void PluginSettings_NormalizesFontFamilyAndWeight()
    {
        var settings = new PluginSettings
        {
            LyricsOriginalFontFamily = "   ",
            LyricsOriginalFontWeight = 123
        };

        Assert.Equal(string.Empty, settings.LyricsOriginalFontFamily);
        Assert.Equal(0, settings.LyricsOriginalFontWeight);
        Assert.Equal(0, settings.LyricsOriginalFontWeightIndex);

        settings.LyricsOriginalFontWeightIndex = LyricsTypography.ToFontWeightIndex(600);
        Assert.Equal(600, settings.LyricsOriginalFontWeight);
    }

    [Fact]
    public void PluginSettings_RaisesChangeNotificationForFontProperties()
    {
        var settings = new PluginSettings();
        var changed = new List<string?>();
        settings.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        settings.LyricsTranslationFontFamily = "Noto Sans JP";
        settings.LyricsTranslationFontWeight = 500;

        Assert.Contains(nameof(PluginSettings.LyricsTranslationFontFamily), changed);
        Assert.Contains(nameof(PluginSettings.LyricsTranslationFontWeight), changed);
        Assert.Contains(nameof(PluginSettings.LyricsTranslationFontWeightIndex), changed);
    }
}
