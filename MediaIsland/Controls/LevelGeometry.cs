using Avalonia;

namespace MediaIsland.Controls;

/// <summary>音频电平两种样式的几何。从原 SpectrumGeometry 迁出并补底轨。</summary>
public static class LevelGeometry
{
    private const double PeakTickWidth = 2;
    private const double PanPadding = 10;
    private const double CenterTickHeight = 8;

    /// <summary>
    /// 电平表：底轨（静止形态）+ RMS 填充 + 峰值刻线。刻线右缘对齐峰值位置再夹回控件内——
    /// 峰值为 1 时不越右边界，为 0 时不越左边界；控件宽度为 0 时上下界会颠倒，用 Math.Max 兜住。
    /// </summary>
    public static (Rect Track, Rect Fill, Rect PeakTick) Meter(double rms, double peak, double width, double height)
    {
        width = Math.Max(0, width);
        height = Math.Max(0, height);
        var level = Clamp01(rms);
        var peakLevel = Clamp01(peak);
        var tickX = Math.Clamp(width * peakLevel - PeakTickWidth, 0, Math.Max(0, width - PeakTickWidth));
        return (new Rect(0, 0, width, height),
            new Rect(0, 0, width * level, height),
            new Rect(tickX, 0, Math.Min(PeakTickWidth, width), height));
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
        var balance = sum > 1e-6 ? (r - l) / sum : 0;
        var maxRadius = Math.Max(SpectrumGeometry.MinDot, Math.Min(height / 2, width / 6));
        var radius = SpectrumGeometry.MinDot + (maxRadius - SpectrumGeometry.MinDot) * Math.Max(l, r);
        var reach = Math.Max(0, cx - Math.Max(PanPadding, radius));
        var track = new Rect(Math.Min(PanPadding, cx), cy - 1, Math.Max(0, width - PanPadding * 2), Math.Min(2, height));
        var tick = new Rect(cx - 0.5, cy - Math.Min(CenterTickHeight, height) / 2, 1, Math.Min(CenterTickHeight, height));
        return (track, tick, new Point(cx + balance * reach, cy), radius);
    }

    private static double Clamp01(double v) => double.IsNaN(v) ? 0 : Math.Clamp(v, 0, 1);
}
