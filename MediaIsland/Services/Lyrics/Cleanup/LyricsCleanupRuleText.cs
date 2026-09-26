using MediaIsland.Services.Lyrics.Models;

namespace MediaIsland.Services.Lyrics.Cleanup;

/// <summary>设置页「每行一条」多行文本与规则列表之间的转换。</summary>
public static class LyricsCleanupRuleText
{
    public static List<string> ParseLines(string? text) =>
        LyricsSourceSettings.NormalizeRuleList((text ?? string.Empty).Split('\n'));

    public static string Format(IEnumerable<string> rules) => string.Join(Environment.NewLine, rules);

    /// <summary>无法编译、或能匹配空串（会删光歌词）的正则归入 Invalid。</summary>
    public static (List<string> Valid, List<string> Invalid) PartitionRegexes(IEnumerable<string> patterns)
    {
        var valid = new List<string>();
        var invalid = new List<string>();
        foreach (var pattern in patterns)
        {
            (LyricsCleanupOptions.TryCompile(pattern) is null ? invalid : valid).Add(pattern);
        }

        return (valid, invalid);
    }
}
