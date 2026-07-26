using Avalonia.Media;
using MediaIsland.Models;
using MediaIsland.Services.Lyrics.Models;

namespace MediaIsland.Services.Lyrics;

/// <summary>
/// 按展示部分（原文 / 翻译 / 音译）解析歌词字体与字重。
/// 未配置时回退到 ClassIsland 岛屿的全局字体，保证默认外观不变。
/// </summary>
internal static class LyricsTypography
{
    /// <summary>
    /// 设置页字重下拉的取值表，索引 0 表示跟随全局。
    /// </summary>
    internal static readonly int[] FontWeightOptions =
        [0, 100, 200, 300, 400, 500, 600, 700, 800, 900];

    internal static int NormalizeFontWeight(int weight) =>
        Array.IndexOf(FontWeightOptions, weight) >= 0 ? weight : 0;

    internal static int ToFontWeightIndex(int weight)
    {
        var index = Array.IndexOf(FontWeightOptions, weight);
        return index < 0 ? 0 : index;
    }

    internal static int FromFontWeightIndex(int index) =>
        index >= 0 && index < FontWeightOptions.Length ? FontWeightOptions[index] : 0;

    internal static string NormalizeFontFamily(string? family) =>
        string.IsNullOrWhiteSpace(family) ? string.Empty : family.Trim();

    internal static FontFamily ResolveFontFamily(string? configured, FontFamily fallback)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return fallback;
        }

        try
        {
            return new FontFamily(configured.Trim());
        }
        catch (Exception)
        {
            // 字体名被手动改坏时不应让歌词消失，回退到全局字体。
            return fallback;
        }
    }

    internal static FontWeight ResolveFontWeight(int configured, FontWeight fallback)
    {
        var weight = NormalizeFontWeight(configured);
        return weight > 0 ? (FontWeight)weight : fallback;
    }

    internal static LyricsPartTypography Resolve(
        PluginSettings? settings,
        LyricsDisplayPart part,
        FontFamily fallbackFamily,
        FontWeight fallbackWeight)
    {
        var (family, weight) = GetConfigured(settings, part);
        return new LyricsPartTypography(
            ResolveFontFamily(family, fallbackFamily),
            ResolveFontWeight(weight, fallbackWeight));
    }

    private static (string? Family, int Weight) GetConfigured(PluginSettings? settings, LyricsDisplayPart part)
    {
        if (settings == null)
        {
            return (null, 0);
        }

        return part switch
        {
            LyricsDisplayPart.Translation
                => (settings.LyricsTranslationFontFamily, settings.LyricsTranslationFontWeight),
            LyricsDisplayPart.Romanization
                => (settings.LyricsRomanizationFontFamily, settings.LyricsRomanizationFontWeight),
            _ => (settings.LyricsOriginalFontFamily, settings.LyricsOriginalFontWeight)
        };
    }
}

internal readonly record struct LyricsPartTypography(FontFamily Family, FontWeight Weight);
