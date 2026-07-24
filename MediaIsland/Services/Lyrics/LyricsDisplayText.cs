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
    /// 是否实际展示原文。翻译/音译缺失时会回退到原文。
    /// </summary>
    public static bool UsesOriginalText(LyricsLine line, LyricsDisplayPart part)
    {
        return part switch
        {
            LyricsDisplayPart.Translation => string.IsNullOrWhiteSpace(line.Translation),
            LyricsDisplayPart.Romanization => string.IsNullOrWhiteSpace(line.Romanization),
            _ => true
        };
    }
}
