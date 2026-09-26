using Avalonia;

namespace MediaIsland.Controls;

/// <summary>起拍后外扩的一圈。Age01 从 0 长到 1 即寿终。</summary>
public readonly record struct RippleRing(double Age01, double Strength);

/// <summary>
/// 音频能量六种样式的几何。每个方法都不假设能量已被裁剪（NaN 视作 0），
/// 且在零能量时仍返回可见图元——静止形态是「组件还在」的唯一证据。
/// </summary>
public static class EnergyGeometry
{
    /// <summary>心电图的样本数：60fps × 3s。</summary>
    public const int HistoryCapacity = 180;

    public const double RippleLifetimeSeconds = 0.9;

    private const double DotRadius = 3;
    private const double DotGap = 10;

    /// <summary>呼吸光点：半径从最小圆点长到短边一半的 0.6，柔光半径为 2.4 倍。</summary>
    public static (Point Center, double Radius, double GlowRadius) Orb(double e, double w, double h)
    {
        var (cx, cy, shortHalf) = Frame(w, h);
        var max = Math.Max(SpectrumGeometry.MinDot, shortHalf * 0.6);
        var r = SpectrumGeometry.MinDot + (max - SpectrumGeometry.MinDot) * E(e);
        return (new Point(cx, cy), r, r * 2.4);
    }

    /// <summary>节拍涟漪：中心点 2.5→6 缩放；每圈半径从核心外缘长到短边一半，不透明度随年龄线性降到 0。</summary>
    public static (Point Center, double CoreRadius, (double Radius, double Opacity)[] Rings) Ripple(
        double e, IReadOnlyList<RippleRing> rings, double w, double h)
    {
        var (cx, cy, shortHalf) = Frame(w, h);
        var core = 2.5 + 3.5 * E(e);
        var result = new (double, double)[rings.Count];
        for (var k = 0; k < rings.Count; k++)
        {
            var age = Clamp01(rings[k].Age01);
            var start = core + 1.5;
            result[k] = (start + age * Math.Max(0, shortHalf - start), (1 - age) * 0.8 * Clamp01(rings[k].Strength));
        }

        return (new Point(cx, cy), core, result);
    }

    /// <summary>光带：满宽、竖直居中，粗 1→4，不透明度 0.35→1。</summary>
    public static (Rect Line, double Opacity) GlowLine(double e, double w, double h)
    {
        var (_, cy, _) = Frame(w, h);
        var thickness = 1 + 3 * E(e);
        return (new Rect(0, cy - thickness / 2, Math.Max(0, w), thickness), 0.35 + 0.65 * E(e));
    }

    /// <summary>三点跃动：三点错开 0.9 弧度相位上跳，振幅 = 能量 × 可用半高。零能量时三点落在中线。</summary>
    public static (Point[] Centers, double Radius) Dots(double e, double phaseSeconds, double w, double h)
    {
        var (cx, cy, _) = Frame(w, h);
        var gap = Math.Min(DotGap, Math.Max(0, w) / 3);
        var amplitude = Math.Max(0, cy - DotRadius) * E(e);
        var phase = double.IsFinite(phaseSeconds) ? phaseSeconds : 0;
        var centers = new Point[3];
        for (var k = -1; k <= 1; k++)
        {
            centers[k + 1] = new Point(cx + k * gap, cy - Math.Abs(Math.Sin(phase * 6 + k * 0.9)) * amplitude);
        }

        return (centers, DotRadius);
    }

    /// <summary>
    /// 能量心电图：最新样本在右缘，向左每格 w/(capacity-1)。不足两点时返回贴底基线——
    /// 心电图没有数据的样子就是一条平线。
    /// </summary>
    public static Point[] History(IReadOnlyList<float> samples, int capacity, double w, double h)
    {
        w = Math.Max(0, w);
        h = Math.Max(0, h);
        var baseline = Math.Max(0, h - 1);
        if (samples.Count < 2 || capacity < 2)
        {
            return [new Point(0, baseline), new Point(w, baseline)];
        }

        var count = Math.Min(samples.Count, capacity);
        var step = w / (capacity - 1);
        var points = new Point[count];
        for (var k = 0; k < count; k++)
        {
            var v = SpectrumGeometry.Clamp01(samples[samples.Count - count + k]);
            points[k] = new Point(w - (count - 1 - k) * step, baseline - v * Math.Max(0, h - 2));
        }

        return points;
    }

    /// <summary>四拍计数：四点等距，整体居中。亮哪一个由组件决定，几何只给位置。</summary>
    public static (Point[] Centers, double Radius) Beat(double w, double h)
    {
        var (cx, cy, _) = Frame(w, h);
        var gap = Math.Min(12, Math.Max(0, w) / 4);
        var centers = new Point[4];
        for (var k = 0; k < 4; k++) centers[k] = new Point(cx + (k - 1.5) * gap, cy);
        return (centers, DotRadius);
    }

    private static (double Cx, double Cy, double ShortHalf) Frame(double w, double h)
    {
        w = Math.Max(0, w);
        h = Math.Max(0, h);
        return (w / 2, h / 2, Math.Min(w, h) / 2);
    }

    private static double E(double e) => Clamp01(e);

    private static double Clamp01(double v) => double.IsNaN(v) ? 0 : Math.Clamp(v, 0, 1);
}
