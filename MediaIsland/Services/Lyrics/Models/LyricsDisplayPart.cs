namespace MediaIsland.Services.Lyrics.Models;

/// <summary>
/// 歌词展示的文本部分。
/// </summary>
public enum LyricsDisplayPart
{
    /// <summary>
    /// 原文。
    /// </summary>
    Original = 0,

    /// <summary>
    /// 翻译；若当前行没有翻译则回退到原文。
    /// </summary>
    Translation = 1,

    /// <summary>
    /// 音译；若当前行没有音译则回退到原文。
    /// </summary>
    Romanization = 2
}
