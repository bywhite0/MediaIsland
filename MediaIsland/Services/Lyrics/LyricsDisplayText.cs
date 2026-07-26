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
            LyricsDisplayPart.Romanization when !string.IsNullOrWhiteSpace(line.Romanization)
                => line.Romanization!,
            _ => line.Text ?? string.Empty
        };
    }

    /// <summary>
    /// 解析实际生效的展示部分。翻译/音译缺失时回退到原文，
    /// 字体与字重按这个结果取用，避免回退到原文的行套用翻译字体。
    /// </summary>
    public static LyricsDisplayPart ResolveEffectivePart(LyricsLine line, LyricsDisplayPart part)
    {
        return part switch
        {
            LyricsDisplayPart.Translation when !string.IsNullOrWhiteSpace(line.Translation)
                => LyricsDisplayPart.Translation,
            LyricsDisplayPart.Romanization when !string.IsNullOrWhiteSpace(line.Romanization)
                => LyricsDisplayPart.Romanization,
            _ => LyricsDisplayPart.Original
        };
    }

    /// <summary>
    /// 是否实际展示原文。翻译/音译缺失时会回退到原文。
    /// </summary>
    public static bool UsesOriginalText(LyricsLine line, LyricsDisplayPart part)
    {
        return ResolveEffectivePart(line, part) == LyricsDisplayPart.Original;
    }
}
