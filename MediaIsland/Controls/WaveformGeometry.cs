using Avalonia;

namespace MediaIsland.Controls;

/// <summary>示波器折线。从原 SpectrumGeometry 迁出——波形是时域信号，与频谱几何无关。</summary>
public static class WaveformGeometry
{
    /// <summary>
    /// 首点 x=0、末点 x=width。y 轴翻转：屏幕坐标向下增长，而波形正半轴该朝上——不翻会得到
    /// 上下颠倒的波形，静音时看不出区别，有信号时才发现。少于两点时画中线：那就是静止形态。
    /// </summary>
    public static Point[] Oscilloscope(IReadOnlyList<float> waveform, double width, double height)
    {
        width = Math.Max(0, width);
        height = Math.Max(0, height);
        var middle = height / 2;
        if (waveform.Count < 2)
        {
            return [new Point(0, middle), new Point(width, middle)];
        }

        var points = new Point[waveform.Count];
        var step = width / (waveform.Count - 1);
        for (var i = 0; i < waveform.Count; i++)
        {
            var value = float.IsNaN(waveform[i]) ? 0f : Math.Clamp(waveform[i], -1f, 1f);
            points[i] = new Point(i * step, middle - value * middle);
        }

        return points;
    }
}
