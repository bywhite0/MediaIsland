using Avalonia;
using MediaIsland.Controls;
using Xunit;

namespace MediaIsland.Tests.Components;

/// <summary>示波器折线的值 → 坐标。从 SpectrumGeometryTests 迁来——波形是时域信号，与频谱几何无关。</summary>
public class WaveformGeometryTests
{
    private const double Width = 100;
    private const double Height = 40;

    [Fact]
    public void Oscilloscope_SpansTheFullWidth()
    {
        var points = WaveformGeometry.Oscilloscope(new float[128], width: 200, Height);

        Assert.Equal(128, points.Length);
        Assert.Equal(0, points[0].X);
        Assert.Equal(200, points[^1].X);
    }

    [Fact]
    public void Oscilloscope_SilentWaveform_SitsOnTheCenterline()
    {
        var points = WaveformGeometry.Oscilloscope(new float[64], Width, Height);

        Assert.All(points, point => Assert.Equal(Height / 2, point.Y));
    }

    [Fact]
    public void Oscilloscope_MapsAmplitudeWithPositiveUp()
    {
        // 屏幕坐标 y 向下增长，而波形的正半轴该朝上——这里必须翻转，
        // 不翻会得到一个上下颠倒的波形，静音时看不出区别，有信号时才发现。
        var points = WaveformGeometry.Oscilloscope([1f, -1f], Width, Height);

        Assert.Equal(0, points[0].Y);
        Assert.Equal(Height, points[1].Y);
    }

    [Theory]
    [InlineData(2f)]
    [InlineData(-2f)]
    public void Oscilloscope_OutOfRangeAmplitude_StaysInsideTheControl(float value)
    {
        var points = WaveformGeometry.Oscilloscope([value, value], Width, Height);

        Assert.All(points, point => Assert.InRange(point.Y, 0, Height));
    }

    /// <summary>单点不足以连线，按静止形态画中线；同时不做除零。</summary>
    [Fact]
    public void Oscilloscope_SinglePoint_DoesNotDivideByZero()
    {
        var points = WaveformGeometry.Oscilloscope([0.5f], Width, Height);

        Assert.Equal(new[] { new Point(0, Height / 2), new Point(Width, Height / 2) }, points);
    }

    /// <summary>静止形态：没有波形时画中线，而不是什么都不画。</summary>
    [Fact]
    public void Oscilloscope_EmptyWaveform_IsTheCenterLine()
    {
        var points = WaveformGeometry.Oscilloscope([], Width, Height);

        Assert.Equal(new[] { new Point(0, Height / 2), new Point(Width, Height / 2) }, points);
    }

    [Fact]
    public void Oscilloscope_NaNSamples_SitOnTheCenterLine()
    {
        var points = WaveformGeometry.Oscilloscope([float.NaN, float.NaN], Width, Height);

        Assert.All(points, p => Assert.Equal(Height / 2, p.Y, 6));
    }

    [Fact]
    public void Oscilloscope_ZeroSize_DoesNotThrow()
    {
        _ = WaveformGeometry.Oscilloscope([0.5f, -0.5f], 0, 0);
    }
}
