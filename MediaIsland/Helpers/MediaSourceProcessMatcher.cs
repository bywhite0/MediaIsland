using System.IO;

namespace MediaIsland.Helpers;

/// <summary>
/// 把 SMTC 的 AUMID 拆成可用于模糊匹配进程路径的标识变体，并为候选进程打分。
/// </summary>
/// <remarks>
/// 1.0.8.0 靠「全进程扫描 + 变体模糊匹配可执行文件路径」定位播放器，因此
/// <c>cn.toside.music.desktop</c> 这类反向域名式 AUMID 能对上实际进程
/// <c>lx-music-desktop.exe</c>；1.1.0.0 起改为要求 AUMID 去扩展名后恰好等于进程名，
/// 这类应用与多进程结构的网易云音乐都会落空。此处恢复变体匹配，并额外过滤过短变体
/// （如 <c>cn</c> / <c>net</c>）避免误命中无关进程。
/// </remarks>
public static class MediaSourceProcessMatcher
{
    /// <summary>变体最短长度，低于此长度的片段区分度不足，容易误匹配。</summary>
    private const int MinVariantLength = 3;

    private static readonly char[] Separators = ['.', '!', '_'];

    private static readonly string[] NoiseSegments = ["com", "github", "exe"];

    /// <summary>文件名与 AUMID 基名完全一致时的加分，确保优先选中主进程。</summary>
    private const int ExactNameScore = 100;

    /// <summary>拥有主窗口时的加分，主程序通常有窗口，辅助进程通常没有。</summary>
    private const int MainWindowScore = 10;

    /// <summary>
    /// 生成用于匹配进程路径的标识变体，按长度降序排列（长的更具体，优先匹配）。
    /// </summary>
    public static IReadOnlyList<string> GetIdentifierVariants(string sourceApp)
    {
        if (string.IsNullOrWhiteSpace(sourceApp))
        {
            return [];
        }

        var variants = new List<string>();
        foreach (var segment in sourceApp.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var variant = segment;
            foreach (var noise in NoiseSegments)
            {
                variant = variant.Replace(noise, string.Empty, StringComparison.OrdinalIgnoreCase);
            }

            variant = variant.Trim();
            if (variant.Length >= MinVariantLength)
            {
                variants.Add(variant);
            }
        }

        // 原始 AUMID 兜底，保证至少有一个变体可用。
        if (sourceApp.Trim().Length >= MinVariantLength)
        {
            variants.Add(sourceApp.Trim());
        }

        return variants
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(variant => variant.Length)
            .ToList();
    }

    /// <summary>
    /// 为候选进程打分，0 表示不匹配。分数越高越可能是播放器本体。
    /// </summary>
    public static int ScoreCandidate(
        string sourceApp,
        string processPath,
        bool hasMainWindow,
        IReadOnlyList<string> variants)
    {
        if (string.IsNullOrWhiteSpace(processPath) || variants.Count == 0)
        {
            return 0;
        }

        var matchedLength = variants
            .Where(variant => processPath.Contains(variant, StringComparison.OrdinalIgnoreCase))
            .Select(variant => variant.Length)
            .DefaultIfEmpty(0)
            .Max();
        if (matchedLength == 0)
        {
            return 0;
        }

        var score = matchedLength;
        if (hasMainWindow)
        {
            score += MainWindowScore;
        }

        if (IsExactNameMatch(sourceApp, processPath))
        {
            score += ExactNameScore;
        }

        return score;
    }

    private static bool IsExactNameMatch(string sourceApp, string processPath)
    {
        var sourceName = Path.GetFileNameWithoutExtension(sourceApp);
        var processName = Path.GetFileNameWithoutExtension(processPath);
        return !string.IsNullOrWhiteSpace(sourceName) &&
               string.Equals(sourceName, processName, StringComparison.OrdinalIgnoreCase);
    }
}
