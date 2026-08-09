using Avalonia;

namespace MediaIsland.Controls;

/// <summary>
/// 可视化的四种形态。律动条独立成一种而非电平表的开关：
/// 两者几何相同（一条填充条），但语义是不同种类——
/// 电平表是仪表（快起慢落 + 峰值保持，如实报告音量），
/// 律动条是装饰（缓起缓落、可带过冲，跟随节奏）。
/// 做成开关会让「灵敏度/衰减」这组配置的含义随开关漂移。
///
/// 声谱图不在此列：它是唯一需要位图管线（WriteableBitmap 逐列滚动）的形态，
/// 且岛内高度通常只有几十像素，纵向频率分辨率被压得没有意义。
/// </summary>
public enum SpectrumVisualMode
{
    /// <summary>律动条：限频段能量归约的单条。默认形态——岛内槽位窄，它在任何尺寸都读得清。</summary>
    Pulse,

    Bars,

    Oscilloscope,

    LevelMeter
}

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
            var value = Math.Clamp(bands[i], 0f, 1f);
            var x = i * slot;
            result[i] = mirrored
                ? MirroredBar(x, barWidth, value, height)
                : new Rect(x, height * (1 - value), barWidth, height * value);
        }

        return result;
    }

    public static Rect Pulse(double energy, double width, double height, bool mirrored)
    {
        var value = Math.Clamp(energy, 0, 1);
        return mirrored
            ? MirroredBar(0, Math.Max(0, width), value, height)
            : new Rect(0, height * (1 - value), Math.Max(0, width), height * value);
    }

    /// <summary>以垂直中线上下对称：满值时充满，零值时退化为中线上的零高矩形。</summary>
    private static Rect MirroredBar(double x, double barWidth, double value, double height)
    {
        var half = height * value / 2;
        return new Rect(x, height / 2 - half, barWidth, half * 2);
    }

    /// <summary>
    /// 示波器折线。返回点数等于波形长度，首点 x=0、末点 x=width。
    /// y 轴翻转：屏幕坐标向下增长，而波形的正半轴该朝上——不翻会得到一个
    /// 上下颠倒的波形，静音时看不出区别，有信号时才发现。
    /// </summary>
    public static Point[] Oscilloscope(IReadOnlyList<float> waveform, double width, double height)
    {
        if (waveform.Count == 0)
        {
            return [];
        }

        var points = new Point[waveform.Count];
        // 单点时不做除零；此时该点落在最左侧。
        var step = waveform.Count > 1 ? width / (waveform.Count - 1) : 0;
        var middle = height / 2;

        for (var i = 0; i < waveform.Count; i++)
        {
            var value = Math.Clamp(waveform[i], -1f, 1f);
            points[i] = new Point(i * step, middle - value * middle);
        }

        return points;
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
}
