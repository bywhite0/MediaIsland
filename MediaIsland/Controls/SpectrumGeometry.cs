using Avalonia;

namespace MediaIsland.Controls;

/// <summary>
/// 值 → 坐标的映射。与 Avalonia 渲染上下文无关，故可在无 UI 环境下单测；
/// <c>Render</c> 本身测不了，把这层抽出来是本期渲染逻辑唯一可验证的部分。
///
/// 每个方法都不假设入参已被上游裁剪。Avalonia 的 <see cref="Rect"/> 接受负的
/// Width/Height 而不抛，画出来是空白或错位——排查时会先怀疑数据而不是几何。
/// </summary>
public static class SpectrumGeometry
{
    private const double PeakTickWidth = 2;

    public static Rect[] Bars(
        IReadOnlyList<float> bands, double width, double height, double gap, bool mirrored)
    {
        if (bands.Count == 0)
        {
            return [];
        }

        var slot = width / bands.Count;
        // 槽位比间隙还窄时夹到 0：岛上的槽位可以被拖得很窄，而段数是用户配的，
        // 两者没有约束关系。
        var barWidth = Math.Max(0, slot - gap);
        var result = new Rect[bands.Count];

        for (var i = 0; i < bands.Count; i++)
        {
            var value = Clamp01(bands[i]);
            var x = i * slot;
            result[i] = mirrored
                ? MirroredBar(x, barWidth, value, height)
                : new Rect(x, height * (1 - value), barWidth, height * value);
        }

        return result;
    }

    /// <summary>以垂直中线上下对称：满值时充满，零值时退化为中线上的零高矩形。</summary>
    private static Rect MirroredBar(double x, double barWidth, double value, double height)
    {
        var half = height * value / 2;
        return new Rect(x, height / 2 - half, barWidth, half * 2);
    }

    /// <summary>电平表：RMS 填充条 + 峰值刻线。</summary>
    public static (Rect Fill, Rect PeakTick) LevelMeter(
        double rms, double peak, double width, double height)
    {
        var level = Math.Clamp(rms, 0, 1);
        var peakLevel = Math.Clamp(peak, 0, 1);

        var fill = new Rect(0, 0, Math.Max(0, width * level), height);

        // 刻线右缘对齐峰值位置，再夹回控件内——峰值为 1 时不越右边界，为 0 时不越左边界。
        // 控件宽度为 0（布局完成前）时上下界会颠倒，故用 Math.Max 兜住。
        var tickX = Math.Clamp(width * peakLevel - PeakTickWidth, 0, Math.Max(0, width - PeakTickWidth));
        return (fill, new Rect(tickX, 0, PeakTickWidth, height));
    }

    public const int SpectrogramRows = 6;
    public const int SpectrogramColumns = 40;
    public const double PeakCapThickness = 1.5;
    public const double RidgeBaseline = 1.5;

    /// <summary>
    /// 胶囊：以水平中线对称，最矮时是一个圆点（高 = 宽），而不是消失——静止时仍看得出组件在。
    /// 宽度取「槽宽减间距」与「半个槽宽」的较小者：频段少时槽很宽，胶囊撑满会变成一排方块。
    /// </summary>
    public static Rect[] Capsules(IReadOnlyList<float> bands, double width, double height, double gap)
    {
        if (bands.Count == 0 || width <= 0 || height <= 0) return [];

        var slot = width / bands.Count;
        var capsuleWidth = Math.Max(0, Math.Min(slot - gap, slot / 2));
        var result = new Rect[bands.Count];
        for (var i = 0; i < bands.Count; i++)
        {
            var h = Math.Max(capsuleWidth, Clamp01(bands[i]) * height);
            h = Math.Min(h, height);
            result[i] = new Rect(i * slot + (slot - capsuleWidth) / 2, (height - h) / 2, capsuleWidth, h);
        }

        return result;
    }

    /// <summary>
    /// 峰值帽：贴在峰值位置的一条细线，夹在控件内。峰值为 0 时落在底边——静止形态就是一排贴底的帽。
    /// 镜像时每段上下各一顶，关于水平中线对称。
    /// </summary>
    public static Rect[] PeakCaps(IReadOnlyList<float> peaks, double width, double height, double gap, bool mirrored)
    {
        if (peaks.Count == 0 || width <= 0 || height <= 0) return [];

        var slot = width / peaks.Count;
        var capWidth = Math.Max(0, slot - gap);
        var thickness = Math.Min(PeakCapThickness, height / (mirrored ? 2 : 1));
        var result = new List<Rect>(peaks.Count * (mirrored ? 2 : 1));
        for (var i = 0; i < peaks.Count; i++)
        {
            var p = Clamp01(peaks[i]);
            var x = i * slot;
            if (mirrored)
            {
                var half = height * p / 2;
                result.Add(new Rect(x, Math.Clamp(height / 2 - half - thickness, 0, height / 2 - thickness), capWidth, thickness));
                result.Add(new Rect(x, Math.Clamp(height / 2 + half, height / 2, height - thickness), capWidth, thickness));
            }
            else
            {
                result.Add(new Rect(x, Math.Clamp(height * (1 - p) - thickness, 0, height - thickness), capWidth, thickness));
            }
        }

        return result.ToArray();
    }

    /// <summary>
    /// 山脊的控制点：首点 x=0、末点 x=width，presenter 用三次贝塞尔（控制点取相邻两点的水平中点）连起来。
    /// 非镜像时基线在底部上方 <see cref="RidgeBaseline"/>；镜像时点在上半部，presenter 关于中线再画一遍。
    /// </summary>
    public static Point[] Ridge(IReadOnlyList<float> bands, double width, double height, bool mirrored)
    {
        width = Math.Max(0, width);
        height = Math.Max(0, height);
        if (bands.Count < 2)
        {
            var y = mirrored ? height / 2 : Math.Max(0, height - RidgeBaseline);
            return [new Point(0, y), new Point(width, y)];
        }

        var points = new Point[bands.Count];
        var step = width / (bands.Count - 1);
        for (var i = 0; i < bands.Count; i++)
        {
            var v = Clamp01(bands[i]);
            var y = mirrored
                ? height / 2 - v * height / 2
                : Math.Max(0, height - RidgeBaseline - v * Math.Max(0, height - RidgeBaseline * 2));
            points[i] = new Point(i * step, y);
        }

        return points;
    }

    /// <summary>
    /// 迷你声谱带：固定 <see cref="SpectrogramRows"/> 行 × <paramref name="capacity"/> 列，
    /// 最新一列在最右，行 0（最低频）在最底。历史不足时左侧补零值格——暗格网格就是静止形态。
    /// </summary>
    public static (Rect Cell, float Value)[] SpectrogramCells(
        IReadOnlyList<float[]> columns, int capacity, double width, double height)
    {
        if (capacity <= 0 || width <= 0 || height <= 0) return [];

        var cellW = width / capacity;
        var cellH = height / SpectrogramRows;
        var inset = Math.Min(0.5, Math.Min(cellW, cellH) / 4);
        var result = new (Rect, float)[capacity * SpectrogramRows];
        var offset = capacity - Math.Min(columns.Count, capacity);
        var skip = Math.Max(0, columns.Count - capacity);
        for (var c = 0; c < capacity; c++)
        {
            var column = c >= offset ? columns[skip + c - offset] : null;
            for (var r = 0; r < SpectrogramRows; r++)
            {
                var value = column is not null && r < column.Length ? Clamp01(column[r]) : 0f;
                var rect = new Rect(c * cellW + inset, height - (r + 1) * cellH + inset,
                    Math.Max(0, cellW - inset * 2), Math.Max(0, cellH - inset * 2));
                result[c * SpectrogramRows + r] = (rect, value);
            }
        }

        return result;
    }

    /// <summary>低中高三球：等距排开，半径从最小圆点长到槽高的一半。</summary>
    public static (Point Center, double Radius)[] Tri(float low, float mid, float high, double width, double height)
    {
        width = Math.Max(0, width);
        height = Math.Max(0, height);
        var maxRadius = Math.Max(MinDot, Math.Min(width / 6, height / 2));
        var cy = height / 2;
        return
        [
            (new Point(width / 6, cy), Radius(low)),
            (new Point(width / 2, cy), Radius(mid)),
            (new Point(width * 5 / 6, cy), Radius(high))
        ];

        double Radius(float v) => MinDot + (maxRadius - MinDot) * Clamp01(v);
    }

    /// <summary>
    /// 十二音环：以控件中心为圆心，从正上方（-90°）顺时针排 12 段，段间留 7° 空隙。
    /// 半径取短边一半减去线宽一半，让环完整落在控件内。
    /// </summary>
    public static (Point Center, double Radius, double Thickness, (double StartDeg, double SweepDeg, float Value)[] Arcs)
        Chroma(IReadOnlyList<float> chroma, double width, double height)
    {
        width = Math.Max(0, width);
        height = Math.Max(0, height);
        var thickness = Math.Max(1, Math.Min(width, height) / 8);
        var radius = Math.Max(0, Math.Min(width, height) / 2 - thickness / 2);
        var arcs = new (double, double, float)[12];
        for (var k = 0; k < 12; k++)
        {
            var value = k < chroma.Count ? Clamp01(chroma[k]) : 0f;
            arcs[k] = (-90 + k * 30 + 3.5, 23, value);
        }

        return (new Point(width / 2, height / 2), radius, thickness, arcs);
    }

    /// <summary>最小圆点半径。三球、呼吸光点、声像光点的静止形态共用这一个尺寸。</summary>
    public const double MinDot = 3;

    /// <summary>NaN 视作 0：上游偶发的坏值不该让整条几何失效。</summary>
    internal static float Clamp01(float v) => float.IsNaN(v) ? 0f : Math.Clamp(v, 0f, 1f);
}
