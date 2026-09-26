using System.Text;
using System.Text.RegularExpressions;
using MediaIsland.Services.Lyrics.Models;

namespace MediaIsland.Services.Lyrics.Cleanup;

/// <summary>
/// 一次歌词清理所用的全部规则。不可变：调用方在设置快照上构造一份，整次解析共用；
/// 不做全局静态钩子，是为了让依赖显式、测试之间互不污染。
/// </summary>
public sealed record LyricsCleanupOptions
{
    /// <summary>单条正则单次匹配的上限，防止用户写出灾难回溯的正则卡住解析。</summary>
    public static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    public static readonly IReadOnlySet<string> DefaultCreditKeywords =
        LyricsCreditRules.Keywords
            .Select(NormalizeKeyword)
            .Where(keyword => keyword.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

    public static readonly IReadOnlyList<Regex> DefaultCreditRegexes =
        LyricsCreditRules.Patterns.Select(TryCompile).OfType<Regex>().ToArray();

    private static readonly char[] ArtistSeparators = [',', '，', '/', '&', ';', '、'];

    public bool StripCredits { get; init; }

    /// <summary>已归一化（见 <see cref="NormalizeKeyword"/>）的职务关键词。</summary>
    public IReadOnlySet<string> CreditKeywords { get; init; } = DefaultCreditKeywords;

    public IReadOnlyList<Regex> CreditRegexes { get; init; } = DefaultCreditRegexes;

    public bool MaskEnabled { get; init; }

    public IReadOnlyList<string> MaskWords { get; init; } = [];

    public IReadOnlyList<Regex> MaskRegexes { get; init; } = [];

    /// <summary>「歌名 - 歌手」行检测用的歌名；null 表示尚未套用曲目元数据。</summary>
    public string? Title { get; init; }

    public IReadOnlyList<string> Artists { get; init; } = [];

    public static LyricsCleanupOptions From(LyricsSourceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var extraKeywords = (settings.CreditKeywords ?? [])
            .Select(NormalizeKeyword)
            .Where(keyword => keyword.Length > 0)
            .ToArray();

        return new LyricsCleanupOptions
        {
            StripCredits = settings.StripCreditLines,
            CreditKeywords = extraKeywords.Length == 0
                ? DefaultCreditKeywords
                : DefaultCreditKeywords.Concat(extraKeywords).ToHashSet(StringComparer.Ordinal),
            CreditRegexes = [.. DefaultCreditRegexes, .. CompileAll(settings.CreditRegexes)],
            MaskEnabled = settings.MaskEnabled,
            MaskWords = (settings.MaskWords ?? [])
                .Where(word => !string.IsNullOrWhiteSpace(word))
                .Select(word => word.Trim())
                .ToArray(),
            MaskRegexes = CompileAll(settings.MaskRegexes)
        };
    }

    /// <summary>把同一套规则套用到具体歌曲。元数据缺失时歌名记为空串，使其不会被再次填充。</summary>
    public LyricsCleanupOptions WithMetadata(LyricsMetadata? metadata) => this with
    {
        Title = metadata?.Title?.Trim() ?? string.Empty,
        Artists = SplitArtists(metadata?.Artist)
    };

    /// <summary>调用方已显式指定歌名时保持不变，否则套用 <paramref name="metadata"/>。</summary>
    public LyricsCleanupOptions WithMetadataIfMissing(LyricsMetadata? metadata) =>
        Title is null ? WithMetadata(metadata) : this;

    /// <summary>NFKC → 小写 → 去掉全部空白，与 lyric-kit 的 normalizeKw 一致。</summary>
    public static string NormalizeKeyword(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        string normalized;
        try
        {
            normalized = value.Normalize(NormalizationForm.FormKC);
        }
        catch (ArgumentException)
        {
            // 含孤立代理项的损坏文本无法做 Unicode 规范化；按原文继续，不让一行坏字拖垮整首歌。
            normalized = value;
        }

        var builder = new StringBuilder(normalized.Length);
        foreach (var c in normalized.ToLowerInvariant())
        {
            if (!char.IsWhiteSpace(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>编译用户或内置正则；语法错误、或能匹配空串（对每一行都成立）时返回 null。</summary>
    public static Regex? TryCompile(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return null;
        }

        try
        {
            var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
            return regex.IsMatch(string.Empty) ? null : regex;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    private static Regex[] CompileAll(IEnumerable<string>? patterns) =>
        (patterns ?? []).Select(TryCompile).OfType<Regex>().ToArray();

    private static IReadOnlyList<string> SplitArtists(string? artist) =>
        string.IsNullOrWhiteSpace(artist)
            ? []
            : artist.Split(ArtistSeparators, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
