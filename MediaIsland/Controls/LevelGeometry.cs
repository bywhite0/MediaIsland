using Avalonia;

namespace MediaIsland.Controls;

/// <summary>音频电平两种样式的几何。从原 SpectrumGeometry 迁出。</summary>
public static class LevelGeometry
{
    private const double PeakTickWidth = 2;

    /// <summary>电平表的圆角。取小值：按半高取圆角时整条变成胶囊，两端过圆。</summary>
    private const double MeterCornerRadius = 3;

    /// <summary>圆角不超过半高：极矮时退化为胶囊而不是画出越界的圆弧。</summary>
    public static double MeterRadius(double height) => Math.Min(MeterCornerRadius, Math.Max(0, height) / 2);
    private const double PanPadding = 10;
    private const double CenterTickHeight = 8;

    /// <summary>
    /// 声像渐入的起点：两声道之和低于它时光点居中，到它的两倍时完全按声像偏移。
    /// 取组件停表阈值（每声道 0.001）的两倍——停表时两声道都低于 0.001，和必低于此值，
    /// 光点因此一定停在中间，而不是按底噪的比例冻结在一侧。
    /// </summary>
    private const double PanSilenceSum = 0.002;

    /// <summary>
    /// 电平表：RMS 填充 + 峰值刻线，<c>Bar</c> 是整条的外框，只用作裁剪区域，不画出来——
    /// 满高的底轨在岛上显得多余（用户 2026-09-26 的取舍），故电平表静止时什么都不画，
    /// 是「每种样式都有静止形态」的唯一例外。刻线右缘对齐峰值位置再夹回控件内——
    /// 峰值为 1 时不越右边界，为 0 时不越左边界；控件宽度为 0 时上下界会颠倒，用 Math.Max 兜住。
    /// 峰值为 0 时刻线零宽，否则静止时左端会游离出一条竖线。
    /// </summary>
    public static (Rect Bar, Rect Fill, Rect PeakTick) Meter(double rms, double peak, double width, double height)
    {
        width = Math.Max(0, width);
        height = Math.Max(0, height);
        var level = Clamp01(rms);
        var peakLevel = Clamp01(peak);
        var tickX = Math.Clamp(width * peakLevel - PeakTickWidth, 0, Math.Max(0, width - PeakTickWidth));
        return (new Rect(0, 0, width, height),
            new Rect(0, 0, width * level, height),
            new Rect(tickX, 0, peakLevel > 0 ? Math.Min(PeakTickWidth, width) : 0, height));
    }

    /// <summary>
    /// 声像光点：横向位置 = (R−L)/(R+L)，两声道都近乎无声时居中（避免 0/0）；
    /// 半径随较响的一侧从最小圆点长到半高，且光点整体不越出左右边界。
    /// </summary>
    public static (Rect Track, Rect CenterTick, Point Dot, double Radius) Pan(
        double left, double right, double width, double height)
    {
        width = Math.Max(0, width);
        height = Math.Max(0, height);
        var l = Clamp01(left);
        var r = Clamp01(right);
        var cx = width / 2;
        var cy = height / 2;

        var sum = l + r;
        // 按能量渐入：近乎无声时比值只反映底噪，一侧的一点点噪声就能把光点推到边缘。
        var fadeIn = Math.Clamp(sum / PanSilenceSum - 1, 0, 1);
        var balance = fadeIn > 0 ? (r - l) / sum * fadeIn : 0;
        var maxRadius = Math.Max(SpectrumGeometry.MinDot, Math.Min(height / 2, width / 6));
        var radius = SpectrumGeometry.MinDot + (maxRadius - SpectrumGeometry.MinDot) * Math.Max(l, r);
        var reach = Math.Max(0, cx - Math.Max(PanPadding, radius));
        var track = new Rect(Math.Min(PanPadding, cx), cy - 1, Math.Max(0, width - PanPadding * 2), Math.Min(2, height));
        var tick = new Rect(cx - 0.5, cy - Math.Min(CenterTickHeight, height) / 2, 1, Math.Min(CenterTickHeight, height));
        return (track, tick, new Point(cx + balance * reach, cy), radius);
    }

    private static double Clamp01(double v) => double.IsNaN(v) ? 0 : Math.Clamp(v, 0, 1);
}
