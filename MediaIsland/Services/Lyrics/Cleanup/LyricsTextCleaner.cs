using System.Text.RegularExpressions;
using MediaIsland.Services.Lyrics.Models;

namespace MediaIsland.Services.Lyrics.Cleanup;

/// <summary>
/// 歌词文本清理：排除制作人员/版权等非歌词行，遮盖屏蔽词。纯函数，不持有状态。
/// </summary>
/// <remarks>
/// 排除判定移植自 lyric-kit v0.6.0 src/clean/stripper.ts
/// （https://github.com/SPlayer-Dev/lyric-kit，AGPL-3.0）。
/// 与原实现的差异：「歌名 - 歌手」行额外要求去掉歌名与歌手后只剩标点或空白。
/// </remarks>
public static class LyricsTextCleaner
{
    /// <summary>「歌名 - 歌手」行只在开头这么多行正文里找。</summary>
    private const int TitleArtistScanLimit = 5;

    private const int MaxBracketRounds = 5;

    private static readonly (string Open, string Close)[] Brackets =
    [
        ("(", ")"), ("（", "）"), ("【", "】"), ("[", "]"), ("{", "}"), ("『", "』"), ("「", "」")
    ];

    /// <summary>无冒号时关键词前缀之后允许紧跟的分隔符（归一化后形态），如「作词-青石」「编曲（林一）」。</summary>
    private static readonly HashSet<char> NoColonSeparators =
        [':', ',', '.', '!', '-', '_', '(', '[', '{', '【', '『', '「', '。', '·'];

    public static IReadOnlyList<LyricsLine> StripCredits(IReadOnlyList<LyricsLine> lines, LyricsCleanupOptions options)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.StripCredits || lines.Count == 0)
        {
            return lines;
        }

        var texts = lines.Select(line => (line.Text ?? string.Empty).Trim()).ToArray();
        var excluded = new bool[lines.Count];
        for (var i = 0; i < lines.Count; i++)
        {
            excluded[i] = texts[i].Length > 0 && IsCreditLine(texts[i], options);
        }

        MarkTitleArtistLines(texts, excluded, options);
        MarkOrphanBackgroundLines(lines, excluded);

        var first = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            if (!excluded[i] && texts[i].Length > 0)
            {
                first = i;
                break;
            }
        }

        if (first < 0)
        {
            return [];
        }

        var last = first;
        for (var i = lines.Count - 1; i > first; i--)
        {
            if (!excluded[i] && texts[i].Length > 0)
            {
                last = i;
                break;
            }
        }

        var kept = new List<LyricsLine>(last - first + 1);
        for (var i = first; i <= last; i++)
        {
            if (!excluded[i])
            {
                kept.Add(lines[i]);
            }
        }

        return kept.Count == lines.Count ? lines : kept;
    }

    /// <summary>
    /// 命中任一正则；或冒号左侧职务名命中关键词（含「词/曲」「编曲Arranger」这类复合）；
    /// 或无冒号时整行等于多字关键词、或以多字关键词开头并紧跟分隔符。
    /// </summary>
    internal static bool IsCreditLine(string text, LyricsCleanupOptions options)
    {
        foreach (var regex in options.CreditRegexes)
        {
            if (SafeIsMatch(regex, text))
            {
                return true;
            }
        }

        var cleaned = StripOuterBrackets(text);
        var colon = cleaned.IndexOfAny([':', '：']);
        if (colon >= 0)
        {
            var rawKey = cleaned[..colon].Trim();
            if (rawKey.Length == 0)
            {
                return false;
            }

            var key = LyricsCleanupOptions.NormalizeKeyword(StripOuterBrackets(rawKey));
            if (key.Length == 0)
            {
                return false;
            }

            if (options.CreditKeywords.Contains(key))
            {
                return true;
            }

            foreach (var keyword in options.CreditKeywords)
            {
                if (key.Length > keyword.Length &&
                    key.StartsWith(keyword, StringComparison.Ordinal) &&
                    IsCompoundRoleSeparator(key[keyword.Length]))
                {
                    return true;
                }
            }

            return false;
        }

        var normalized = LyricsCleanupOptions.NormalizeKeyword(cleaned);
        if (normalized.Length >= 2 && options.CreditKeywords.Contains(normalized))
        {
            return true;
        }

        foreach (var keyword in options.CreditKeywords)
        {
            if (keyword.Length >= 2 &&
                normalized.Length > keyword.Length &&
                normalized.StartsWith(keyword, StringComparison.Ordinal) &&
                NoColonSeparators.Contains(normalized[keyword.Length]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCompoundRoleSeparator(char c) =>
        c is '/' or '&' or '、' or '+' or (>= 'a' and <= 'z');

    /// <summary>剥掉整行两端的外层括号；「开括号…闭括号 + 后续文本」取后续文本。最多剥 5 轮。</summary>
    private static string StripOuterBrackets(string text)
    {
        var processed = text.Trim();
        var changed = true;
        for (var round = 0; changed && round < MaxBracketRounds; round++)
        {
            changed = false;
            foreach (var (open, close) in Brackets)
            {
                if (!processed.StartsWith(open, StringComparison.Ordinal))
                {
                    continue;
                }

                if (processed.EndsWith(close, StringComparison.Ordinal))
                {
                    processed = processed[open.Length..^close.Length].Trim();
                    changed = true;
                    break;
                }

                var closeIndex = processed.IndexOf(close, StringComparison.Ordinal);
                if (closeIndex > -1)
                {
                    var after = processed[(closeIndex + close.Length)..].Trim();
                    if (after.Length > 0)
                    {
                        processed = after;
                        changed = true;
                        break;
                    }
                }
            }
        }

        return processed;
    }

    private static void MarkTitleArtistLines(string[] texts, bool[] excluded, LyricsCleanupOptions options)
    {
        var title = options.Title;
        if (string.IsNullOrWhiteSpace(title) || options.Artists.Count == 0)
        {
            return;
        }

        var scanned = 0;
        for (var i = 0; i < texts.Length && scanned < TitleArtistScanLimit; i++)
        {
            if (excluded[i] || texts[i].Length == 0)
            {
                continue;
            }

            scanned++;
            excluded[i] = IsTitleArtistLine(texts[i], title, options.Artists);
        }
    }

    /// <summary>同时含歌名与某位歌手，且去掉二者后只剩标点或空白——短歌名（如「爱」）不会误伤正文。</summary>
    private static bool IsTitleArtistLine(string text, string title, IReadOnlyList<string> artists)
    {
        if (!text.Contains(title, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var matchedArtists = artists
            .Where(artist => artist.Length > 0 && text.Contains(artist, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matchedArtists.Length == 0)
        {
            return false;
        }

        var remainder = text.Replace(title, string.Empty, StringComparison.OrdinalIgnoreCase);
        foreach (var artist in matchedArtists)
        {
            remainder = remainder.Replace(artist, string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        return !remainder.Any(char.IsLetterOrDigit);
    }

    /// <summary>主行被排除、或前面没有主行的背景行一并排除。</summary>
    private static void MarkOrphanBackgroundLines(IReadOnlyList<LyricsLine> lines, bool[] excluded)
    {
        var mainIndex = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            if (!lines[i].IsBackground)
            {
                mainIndex = i;
                continue;
            }

            if (mainIndex < 0 || excluded[mainIndex])
            {
                excluded[i] = true;
            }
        }
    }

    private static bool SafeIsMatch(Regex regex, string text)
    {
        try
        {
            return regex.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
