using MediaIsland.Services.Lyrics.Models;

namespace MediaIsland.Services.Lyrics;

/// <summary>
/// 根据显示选项解析歌词行的展示文本。
/// </summary>
public static class LyricsDisplayText
{
    public static string Resolve(LyricsLine line, LyricsDisplayPart part)
    {
        return part switch
        {
            LyricsDisplayPart.Translation when !string.IsNullOrWhiteSpace(line.Translation)
                => line.Translation!,
            LyricsDisplayPart.TranslationOnly when !string.IsNullOrWhiteSpace(line.Translation)
                => line.Translation!,
            LyricsDisplayPart.TranslationOnly
                => string.Empty,
            LyricsDisplayPart.Romanization when !string.IsNullOrWhiteSpace(line.Romanization)
                => line.Romanization!,
            LyricsDisplayPart.RomanizationOnly when !string.IsNullOrWhiteSpace(line.Romanization)
                => line.Romanization!,
            LyricsDisplayPart.RomanizationOnly
                => string.Empty,
            // Translation / Romanization 缺失时回退原文；Original 与其它情况同理。
            _ => line.Text ?? string.Empty
        };
    }

    /// <summary>
    /// 解析实际生效的展示部分。
    /// 「无则显示原文」在翻译/音译缺失时回退到原文；
    /// 「无则不显示」在缺失时保留 Only 模式本身，避免误用原文逐字与假名。
    /// 字体与字重按这个结果取用。
    /// </summary>
    public static LyricsDisplayPart ResolveEffectivePart(LyricsLine line, LyricsDisplayPart part)
    {
        return part switch
        {
            LyricsDisplayPart.Translation when !string.IsNullOrWhiteSpace(line.Translation)
                => LyricsDisplayPart.Translation,
            LyricsDisplayPart.TranslationOnly when !string.IsNullOrWhiteSpace(line.Translation)
                => LyricsDisplayPart.Translation,
            LyricsDisplayPart.TranslationOnly
                => LyricsDisplayPart.TranslationOnly,
            LyricsDisplayPart.Romanization when !string.IsNullOrWhiteSpace(line.Romanization)
                => LyricsDisplayPart.Romanization,
            LyricsDisplayPart.RomanizationOnly when !string.IsNullOrWhiteSpace(line.Romanization)
                => LyricsDisplayPart.Romanization,
            LyricsDisplayPart.RomanizationOnly
                => LyricsDisplayPart.RomanizationOnly,
            _ => LyricsDisplayPart.Original
        };
    }

    /// <summary>
    /// 是否实际展示原文。「无则显示原文」在翻译/音译缺失时会回退到原文。
    /// </summary>
    public static bool UsesOriginalText(LyricsLine line, LyricsDisplayPart part)
    {
        return ResolveEffectivePart(line, part) == LyricsDisplayPart.Original;
    }
}
