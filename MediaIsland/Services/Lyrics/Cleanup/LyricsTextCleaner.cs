using System.Text.RegularExpressions;
using MediaIsland.Services.Lyrics.Models;

namespace MediaIsland.Services.Lyrics.Cleanup;

/// <summary>
/// 歌词文本清理：排除制作人员/版权等非歌词行，遮盖屏蔽词。纯函数，不持有状态。
/// </summary>
/// <remarks>
/// 排除判定移植自 lyric-kit v0.6.0 src/clean/stripper.ts
/// （https://github.com/SPlayer-Dev/lyric-kit，AGPL-3.0）。
/// 与原实现的差异：「歌名 - 歌手」行额外要求去掉歌名与歌手后只剩标点或空白；
/// 其 5 行窗口只数留下的正文行，以保证二次清理幂等。
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
        var timedOut = NewTimedOutSet();
        for (var i = 0; i < lines.Count; i++)
        {
            excluded[i] = texts[i].Length > 0 && IsCreditLine(texts[i], options, timedOut);
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
    /// 命中区间内每个 UTF-16 码元替换为一个 <c>*</c>，长度严格不变，
    /// 使逐字时间与注音索引都不漂移。代价：代理对字符会显示为两个 <c>*</c>。
    /// </summary>
    public static IReadOnlyList<LyricsLine> Mask(IReadOnlyList<LyricsLine> lines, LyricsCleanupOptions options)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.MaskEnabled || (options.MaskWords.Count == 0 && options.MaskRegexes.Count == 0))
        {
            return lines;
        }

        var result = new LyricsLine[lines.Count];
        var changed = false;
        var timedOut = NewTimedOutSet();
        for (var i = 0; i < lines.Count; i++)
        {
            result[i] = MaskLine(lines[i], options, timedOut);
            changed |= !ReferenceEquals(result[i], lines[i]);
        }

        return changed ? result : lines;
    }

    /// <summary>先排除后遮盖：遮盖会改动文本，放在前面会让关键词判定失效。</summary>
    public static IReadOnlyList<LyricsLine> Apply(IReadOnlyList<LyricsLine> lines, LyricsCleanupOptions? options) =>
        options is null ? lines : Mask(StripCredits(lines, options), options);

    private static LyricsLine MaskLine(LyricsLine line, LyricsCleanupOptions options, HashSet<Regex> timedOut)
    {
        var text = line.Text ?? string.Empty;
        var ranges = FindMaskRanges(text, options, timedOut);
        var maskedText = ApplyRanges(text, ranges);
        var words = MaskWords(line.Words, text, maskedText, options, timedOut);
        var translation = MaskText(line.Translation, options, timedOut);
        var romanization = MaskText(line.Romanization, options, timedOut);
        var rubySpans = DropMaskedRuby(line.RubySpans, ranges);

        if (ReferenceEquals(maskedText, text) &&
            ReferenceEquals(words, line.Words) &&
            ReferenceEquals(translation, line.Translation) &&
            ReferenceEquals(romanization, line.Romanization) &&
            ReferenceEquals(rubySpans, line.RubySpans))
        {
            return line;
        }

        return line with
        {
            Text = maskedText,
            Words = words,
            Translation = translation,
            Romanization = romanization,
            RubySpans = rubySpans
        };
    }

    private static List<(int Start, int Length)> FindMaskRanges(
        string text,
        LyricsCleanupOptions options,
        HashSet<Regex> timedOut)
    {
        var ranges = new List<(int Start, int Length)>();
        if (text.Length == 0)
        {
            return ranges;
        }

        foreach (var word in options.MaskWords)
        {
            if (word.Length == 0)
            {
                continue;
            }

            // 含拉丁字母的词要求两侧不是 ASCII 字母数字，避免 class 被 ass 遮成 c***s；
            // 汉字不算边界，「ass你好」照样遮。纯 CJK/假名词按子串匹配。
            var needsBoundary = word.Any(char.IsAsciiLetter);
            var index = text.IndexOf(word, StringComparison.OrdinalIgnoreCase);
            while (index >= 0)
            {
                if (!needsBoundary || IsAsciiWordBoundary(text, index, word.Length))
                {
                    ranges.Add((index, word.Length));
                }

                index = text.IndexOf(word, index + 1, StringComparison.OrdinalIgnoreCase);
            }
        }

        foreach (var regex in options.MaskRegexes)
        {
            if (timedOut.Contains(regex))
            {
                continue;
            }

            try
            {
                foreach (Match match in regex.Matches(text))
                {
                    if (match.Length > 0)
                    {
                        ranges.Add((match.Index, match.Length));
                    }
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // 超时的正则放弃本行已找到之外的命中，本次调用余下的文本也不再跑它；其余规则照常生效。
                timedOut.Add(regex);
            }
        }

        return ranges;
    }

    private static bool IsAsciiWordBoundary(string text, int start, int length) =>
        (start == 0 || !char.IsAsciiLetterOrDigit(text[start - 1])) &&
        (start + length >= text.Length || !char.IsAsciiLetterOrDigit(text[start + length]));

    private static string ApplyRanges(string text, List<(int Start, int Length)> ranges)
    {
        if (ranges.Count == 0)
        {
            return text;
        }

        var chars = text.ToCharArray();
        foreach (var (start, length) in ranges)
        {
            chars.AsSpan(start, length).Fill('*');
        }

        return new string(chars);
    }

    private static string? MaskText(string? text, LyricsCleanupOptions options, HashSet<Regex> timedOut) =>
        string.IsNullOrEmpty(text) ? text : ApplyRanges(text, FindMaskRanges(text, options, timedOut));

    /// <summary>
    /// 逐字文本拼起来等于整行时，按累计偏移把整行的遮盖投影到各字（跨字边界的词也能遮住）；
    /// 对不上时退回逐字独立匹配。
    /// </summary>
    private static IReadOnlyList<LyricsWord> MaskWords(
        IReadOnlyList<LyricsWord> words,
        string text,
        string maskedText,
        LyricsCleanupOptions options,
        HashSet<Regex> timedOut)
    {
        if (words.Count == 0)
        {
            return words;
        }

        if (!string.Equals(string.Concat(words.Select(word => word.Text)), text, StringComparison.Ordinal))
        {
            var independent = new LyricsWord[words.Count];
            var changed = false;
            for (var i = 0; i < words.Count; i++)
            {
                var original = words[i].Text ?? string.Empty;
                var masked = MaskText(original, options, timedOut) ?? string.Empty;
                var wordChanged = !ReferenceEquals(masked, original);
                independent[i] = wordChanged ? words[i] with { Text = masked } : words[i];
                changed |= wordChanged;
            }

            return changed ? independent : words;
        }

        if (ReferenceEquals(maskedText, text))
        {
            return words;
        }

        var projected = new LyricsWord[words.Count];
        var offset = 0;
        for (var i = 0; i < words.Count; i++)
        {
            var length = (words[i].Text ?? string.Empty).Length;
            projected[i] = words[i] with { Text = maskedText.Substring(offset, length) };
            offset += length;
        }

        return projected;
    }

    /// <summary>与遮盖区间相交的注音删掉，否则读音会把被遮的字「念」出来。</summary>
    private static IReadOnlyList<LyricsRubySpan>? DropMaskedRuby(
        IReadOnlyList<LyricsRubySpan>? spans,
        List<(int Start, int Length)> ranges)
    {
        if (spans is null || spans.Count == 0 || ranges.Count == 0)
        {
            return spans;
        }

        var kept = spans
            .Where(span => !ranges.Any(range =>
                span.BaseStart < range.Start + range.Length &&
                range.Start < span.BaseStart + span.BaseLength))
            .ToArray();
        return kept.Length == spans.Count ? spans : kept;
    }

    /// <summary>
    /// 命中任一正则；或冒号左侧职务名命中关键词（含「词/曲」「编曲Arranger」这类复合）；
    /// 或无冒号时整行等于多字关键词、或以多字关键词开头并紧跟分隔符。
    /// </summary>
    internal static bool IsCreditLine(string text, LyricsCleanupOptions options, HashSet<Regex> timedOut)
    {
        foreach (var regex in options.CreditRegexes)
        {
            if (SafeIsMatch(regex, text, timedOut))
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

        // 窗口只数会留下的正文行：本遍删掉的行若也计数，二次清理时窗口会右移、多删一行。
        var scanned = 0;
        for (var i = 0; i < texts.Length && scanned < TitleArtistScanLimit; i++)
        {
            if (excluded[i] || texts[i].Length == 0)
            {
                continue;
            }

            excluded[i] = IsTitleArtistLine(texts[i], title, options.Artists);
            if (!excluded[i])
            {
                scanned++;
            }
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

        // * 按字母算：遮盖后的文本在二次清理时不能被当成「只剩标点」。
        return !remainder.Any(c => c == '*' || char.IsLetterOrDigit(c));
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

    /// <summary>一次调用内首次超时的正则记入此集合，余下的行直接跳过它，免得每行都等满超时。</summary>
    private static HashSet<Regex> NewTimedOutSet() => new(ReferenceEqualityComparer.Instance);

    private static bool SafeIsMatch(Regex regex, string text, HashSet<Regex> timedOut)
    {
        if (timedOut.Contains(regex))
        {
            return false;
        }

        try
        {
            return regex.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            timedOut.Add(regex);
            return false;
        }
    }
}
