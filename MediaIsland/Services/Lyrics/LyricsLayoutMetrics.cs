using Avalonia.Media;
using MediaIsland.Services.Lyrics.Models;

namespace MediaIsland.Services.Lyrics;

internal static class LyricsLayoutMetrics
{
    private const double BackgroundFontScale = 0.7;
    private const double CompactBackgroundFontScale = 0.6;
    private const double MinimumBackgroundFontSize = 10;
    private const double CompactMinimumBackgroundFontSize = 8;
    private const double CompactMainLyricsFontSizeReduction = 4;
    private const double MinimumMainLyricsFontSize = 10;

    // 自适应缩放的可读性下限：再小就宁可少显示一行背景人声。
    internal const double MinimumScaledMainFontSize = 9;
    internal const double MinimumScaledBackgroundFontSize = 7;

    // 字号到行高的近似系数。预算与实际占用共用该系数，只有行距是绝对像素。
    internal const double LineHeightFactor = 1.3;

    private const double FitEpsilon = 0.01;

    public static double GetActiveLineFontSize(
        double defaultFontSize,
        int visibleLineCount,
        bool isBackground)
    {
        if (isBackground)
        {
            // AMLL: max(1em * 0.7, 10px); compact layouts shrink a bit further for the island host.
            if (visibleLineCount > 2)
            {
                return Math.Max(
                    CompactMinimumBackgroundFontSize,
                    defaultFontSize * CompactBackgroundFontScale);
            }

            return Math.Max(MinimumBackgroundFontSize, defaultFontSize * BackgroundFontScale);
        }

        return visibleLineCount > 2
            ? Math.Max(MinimumMainLyricsFontSize, defaultFontSize - CompactMainLyricsFontSizeReduction)
            : defaultFontSize;
    }

    /// <summary>
    /// 灵动岛可用高度的保守估计。三行紧凑布局是已验证能完整显示的最高布局，
    /// 因此把它的纵向占用当作预算上限，无需向宿主反查高度（反查会与自身高度构成循环）。
    /// </summary>
    internal static double GetVerticalBudget(double defaultFontSize) =>
        LineHeightFactor *
        ((GetActiveLineFontSize(defaultFontSize, visibleLineCount: 3, isBackground: false) * 2) +
         GetActiveLineFontSize(defaultFontSize, visibleLineCount: 3, isBackground: true));

    /// <summary>
    /// 让活跃歌词行装进纵向预算：先整体等比缩小，缩到可读性下限仍装不下时，
    /// 从末尾开始丢弃背景人声行。两行主歌词各带一行背景时，第二个背景行不再被裁掉。
    /// </summary>
    /// <param name="isBackgroundLine">与活跃行一一对应的背景人声标记。</param>
    /// <param name="defaultFontSize">岛屿正文字号。</param>
    /// <param name="lineSpacing">用户配置的附加行距（像素，可为负）。</param>
    internal static LyricsFitPlan ResolveFitPlan(
        IReadOnlyList<bool> isBackgroundLine,
        double defaultFontSize,
        double lineSpacing)
    {
        ArgumentNullException.ThrowIfNull(isBackgroundLine);

        var keep = new bool[isBackgroundLine.Count];
        var fontSizes = new double[isBackgroundLine.Count];
        if (isBackgroundLine.Count == 0)
        {
            return new LyricsFitPlan(keep, fontSizes, 1);
        }

        Array.Fill(keep, true);
        var budget = GetVerticalBudget(defaultFontSize);
        while (true)
        {
            var keptCount = CountKept(keep);
            var textExtent = 0d;
            var readableScale = 0d;
            for (var i = 0; i < keep.Length; i++)
            {
                if (!keep[i])
                {
                    fontSizes[i] = 0;
                    continue;
                }

                var size = GetActiveLineFontSize(defaultFontSize, keptCount, isBackgroundLine[i]);
                fontSizes[i] = size;
                textExtent += size * LineHeightFactor;
                var floor = isBackgroundLine[i]
                    ? MinimumScaledBackgroundFontSize
                    : MinimumScaledMainFontSize;
                readableScale = Math.Max(readableScale, size > 0 ? Math.Min(1, floor / size) : 1);
            }

            var available = budget - (lineSpacing * Math.Max(0, keptCount - 1));
            if (textExtent <= 0 || textExtent <= available + FitEpsilon)
            {
                return new LyricsFitPlan(keep, fontSizes, 1);
            }

            var scale = available > 0 ? available / textExtent : 0;
            if (scale >= readableScale)
            {
                ApplyScale(fontSizes, keep, scale);
                return new LyricsFitPlan(keep, fontSizes, scale);
            }

            if (!TryDropTrailingBackgroundLine(keep, isBackgroundLine))
            {
                // 没有可丢弃的背景行了：停在可读性下限，宁可轻微溢出也不继续缩小。
                ApplyScale(fontSizes, keep, readableScale);
                return new LyricsFitPlan(keep, fontSizes, readableScale);
            }
        }
    }

    private static void ApplyScale(double[] fontSizes, bool[] keep, double scale)
    {
        for (var i = 0; i < fontSizes.Length; i++)
        {
            fontSizes[i] = keep[i] ? fontSizes[i] * scale : 0;
        }
    }

    private static int CountKept(bool[] keep)
    {
        var count = 0;
        foreach (var item in keep)
        {
            if (item)
            {
                count++;
            }
        }

        return count;
    }

    private static bool TryDropTrailingBackgroundLine(bool[] keep, IReadOnlyList<bool> isBackgroundLine)
    {
        if (CountKept(keep) <= 1)
        {
            return false;
        }

        for (var i = keep.Length - 1; i >= 0; i--)
        {
            if (keep[i] && isBackgroundLine[i])
            {
                keep[i] = false;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Computes the stable host width for a song: the widest line at its largest on-screen font size.
    /// </summary>
    public static double ComputeMaxSongLineWidth(
        IEnumerable<(string Text, bool IsBackground, LyricsDisplayPart Part)> lines,
        double defaultFontSize,
        Func<string, double, LyricsDisplayPart, double> measureTextWidth)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(measureTextWidth);

        var max = 0d;
        foreach (var (text, isBackground, part) in lines)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            // visibleLineCount=1 uses the largest font for each role (main vs background).
            var fontSize = GetActiveLineFontSize(defaultFontSize, visibleLineCount: 1, isBackground);
            var width = measureTextWidth(text, fontSize, part);
            if (width > max)
            {
                max = width;
            }
        }

        return max;
    }

    /// <summary>
    /// Document-level duet: any TTML duet line forces non-duet sides to the left (AMLL layout).
    /// </summary>
    public static bool DocumentHasDuet(IEnumerable<LyricsLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return lines.Any(static line => line.IsDuet);
    }

    /// <summary>
    /// Resolves horizontal text alignment for an active lyric line.
    /// Duet side is right; when the song has any duet, other lines are left; otherwise center.
    /// </summary>
    public static TextAlignment ResolveLineTextAlignment(bool isDuetSide, bool documentHasDuet) =>
        isDuetSide
            ? TextAlignment.Right
            : documentHasDuet
                ? TextAlignment.Left
                : TextAlignment.Center;

    /// <summary>
    /// Full-frame transition only when the active set has no continuity with the previous set.
    /// Partial handoffs (duet overlap, main+BG gaining/losing a second main) rebuild in place.
    /// </summary>
    public static bool ShouldAnimateFullLineTransition(
        bool isShowingActiveLines,
        IReadOnlyList<int> previousIndices,
        IReadOnlyList<int> nextIndices)
    {
        if (!isShowingActiveLines || previousIndices.Count == 0 || nextIndices.Count == 0)
        {
            return true;
        }

        return !previousIndices.Intersect(nextIndices).Any();
    }
}

/// <summary>
/// 活跃歌词行的纵向适配结果。<see cref="KeepLine"/> 与输入一一对应，
/// 被丢弃行的 <see cref="FontSizes"/> 为 0。
/// </summary>
internal sealed record LyricsFitPlan(
    IReadOnlyList<bool> KeepLine,
    IReadOnlyList<double> FontSizes,
    double Scale);
