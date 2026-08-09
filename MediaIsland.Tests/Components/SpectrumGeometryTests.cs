using Avalonia;
using MediaIsland.Controls;
using Xunit;

namespace MediaIsland.Tests.Components;

/// <summary>
/// 值 → 坐标。这是本期渲染逻辑里唯一可验证的部分——<c>Render</c> 要 Avalonia 渲染上下文，
/// 在无 UI 环境下测不了，把几何抽出来才能断言镜像对称性、边界裁剪与不产生负尺寸。
///
/// 负尺寸是这里最该防的一类：Avalonia 的 Rect 接受负的 Width/Height 而不抛，
/// 画出来是空白或错位，排查时会先怀疑数据而不是几何。
/// </summary>
public class SpectrumGeometryTests
{
    private const double Width = 100;
    private const double Height = 40;

    // ---- Bars ----

    [Fact]
    public void Bars_LaysOutLeftToRightWithinBounds()
    {
        var bars = SpectrumGeometry.Bars([0.5f, 0.5f, 0.5f, 0.5f], Width, Height, gap: 2, mirrored: false);

        Assert.Equal(4, bars.Length);
        for (var i = 1; i < bars.Length; i++)
        {
            Assert.True(bars[i].X > bars[i - 1].X, "柱子的 x 必须递增");
        }

        Assert.True(bars[^1].Right <= Width, $"最右柱越界：{bars[^1].Right}");
    }

    [Fact]
    public void Bars_FullValue_FillsTheFullHeightFromTheTop()
    {
        var bars = SpectrumGeometry.Bars([1f, 1f], Width, Height, gap: 2, mirrored: false);

        Assert.All(bars, bar =>
        {
            Assert.Equal(Height, bar.Height);
            Assert.Equal(0, bar.Y);
        });
    }

    [Fact]
    public void Bars_GrowFromTheBottom()
    {
        // 半值时高度一半、顶边在中间——频谱柱从底部长起来是它的基本语义，
        // 从顶部长会得到一个上下颠倒的频谱，而每根柱子的高度都还是对的。
        var bars = SpectrumGeometry.Bars([0.5f], Width, Height, gap: 0, mirrored: false);

        Assert.Equal(Height / 2, bars[0].Height);
        Assert.Equal(Height / 2, bars[0].Y);
    }

    [Fact]
    public void Bars_ZeroValue_ProducesNoNegativeHeight()
    {
        var bars = SpectrumGeometry.Bars([0f, 0f], Width, Height, gap: 2, mirrored: false);

        Assert.All(bars, bar => Assert.Equal(0, bar.Height));
    }

    [Fact]
    public void Bars_Mirrored_IsSymmetricAboutTheVerticalCenter()
    {
        var bars = SpectrumGeometry.Bars([0.5f], Width, Height, gap: 0, mirrored: true);

        // 半值以中线 y=20 对称：上下各伸展 10。
        Assert.Equal(10, bars[0].Y);
        Assert.Equal(20, bars[0].Height);
        Assert.Equal(Height / 2, bars[0].Y + bars[0].Height / 2);
    }

    [Fact]
    public void Bars_MirroredFullValue_FillsEverything()
    {
        var bars = SpectrumGeometry.Bars([1f], Width, Height, gap: 0, mirrored: true);

        Assert.Equal(0, bars[0].Y);
        Assert.Equal(Height, bars[0].Height);
    }

    [Fact]
    public void Bars_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(SpectrumGeometry.Bars([], Width, Height, gap: 2, mirrored: false));
    }

    [Fact]
    public void Bars_WidthNarrowerThanBandCount_DoesNotProduceNegativeWidth()
    {
        // 岛上的槽位可以被拖得很窄，而段数是用户配的——两者没有约束关系。
        var bars = SpectrumGeometry.Bars([0.5f, 0.5f, 0.5f, 0.5f], width: 2, Height, gap: 2, mirrored: false);

        Assert.All(bars, bar => Assert.True(bar.Width >= 0, $"负宽度：{bar.Width}"));
    }

    [Fact]
    public void Bars_GapWiderThanTheSlot_DoesNotProduceNegativeWidth()
    {
        var bars = SpectrumGeometry.Bars([0.5f, 0.5f], Width, Height, gap: 200, mirrored: false);

        Assert.All(bars, bar => Assert.True(bar.Width >= 0, $"负宽度：{bar.Width}"));
    }

    [Theory]
    [InlineData(1.5f)]
    [InlineData(-0.3f)]
    public void Bars_OutOfRangeValues_AreClampedIntoTheControl(float value)
    {
        // 分析层已经 clamp 过，但几何层不假设上游正确——越界会真的画到控件外面。
        var bars = SpectrumGeometry.Bars([value], Width, Height, gap: 0, mirrored: false);

        Assert.InRange(bars[0].Height, 0, Height);
        Assert.InRange(bars[0].Y, 0, Height);
    }

    // ---- Pulse ----

    [Fact]
    public void Pulse_FullEnergy_FillsTheControl()
    {
        var pulse = SpectrumGeometry.Pulse(1.0, Width, Height, mirrored: false);

        Assert.Equal(Height, pulse.Height);
        Assert.Equal(0, pulse.Y);
        Assert.Equal(Width, pulse.Width);
    }

    [Fact]
    public void Pulse_GrowsFromTheBottom()
    {
        var pulse = SpectrumGeometry.Pulse(0.25, Width, Height, mirrored: false);

        Assert.Equal(10, pulse.Height);
        Assert.Equal(30, pulse.Y);
    }

    [Fact]
    public void Pulse_Mirrored_IsSymmetricAboutTheVerticalCenter()
    {
        var pulse = SpectrumGeometry.Pulse(0.5, Width, Height, mirrored: true);

        Assert.Equal(10, pulse.Y);
        Assert.Equal(20, pulse.Height);
    }

    [Fact]
    public void Pulse_ZeroEnergy_HasNoHeight()
    {
        Assert.Equal(0, SpectrumGeometry.Pulse(0, Width, Height, mirrored: false).Height);
    }

    [Theory]
    [InlineData(2.0)]
    [InlineData(-1.0)]
    public void Pulse_OutOfRangeEnergy_IsClamped(double energy)
    {
        var pulse = SpectrumGeometry.Pulse(energy, Width, Height, mirrored: false);

        Assert.InRange(pulse.Height, 0, Height);
    }

    // ---- Oscilloscope ----

    [Fact]
    public void Oscilloscope_SpansTheFullWidth()
    {
        var points = SpectrumGeometry.Oscilloscope(new float[128], width: 200, Height);

        Assert.Equal(128, points.Length);
        Assert.Equal(0, points[0].X);
        Assert.Equal(200, points[^1].X);
    }

    [Fact]
    public void Oscilloscope_SilentWaveform_SitsOnTheCenterline()
    {
        var points = SpectrumGeometry.Oscilloscope(new float[64], Width, Height);

        Assert.All(points, point => Assert.Equal(Height / 2, point.Y));
    }

    [Fact]
    public void Oscilloscope_MapsAmplitudeWithPositiveUp()
    {
        // 屏幕坐标 y 向下增长，而波形的正半轴该朝上——这里必须翻转，
        // 不翻会得到一个上下颠倒的波形，静音时看不出区别，有信号时才发现。
        var points = SpectrumGeometry.Oscilloscope([1f, -1f], Width, Height);

        Assert.Equal(0, points[0].Y);
        Assert.Equal(Height, points[1].Y);
    }

    [Theory]
    [InlineData(2f)]
    [InlineData(-2f)]
    public void Oscilloscope_OutOfRangeAmplitude_StaysInsideTheControl(float value)
    {
        var points = SpectrumGeometry.Oscilloscope([value, value], Width, Height);

        Assert.All(points, point => Assert.InRange(point.Y, 0, Height));
    }

    [Fact]
    public void Oscilloscope_SinglePoint_DoesNotDivideByZero()
    {
        var points = SpectrumGeometry.Oscilloscope([0.5f], Width, Height);

        Assert.Single(points);
        Assert.Equal(0, points[0].X);
    }

    [Fact]
    public void Oscilloscope_EmptyWaveform_ReturnsEmpty()
    {
        Assert.Empty(SpectrumGeometry.Oscilloscope([], Width, Height));
    }

    // ---- LevelMeter ----

    [Fact]
    public void LevelMeter_FillsProportionallyFromTheLeft()
    {
        var (fill, _) = SpectrumGeometry.LevelMeter(rms: 0.5, peak: 0.8, Width, Height);

        Assert.Equal(50, fill.Width);
        Assert.Equal(0, fill.X);
        Assert.Equal(Height, fill.Height);
    }

    [Fact]
    public void LevelMeter_PeakTickTrailsThePeakPosition()
    {
        var (_, tick) = SpectrumGeometry.LevelMeter(rms: 0.5, peak: 0.8, Width, Height);

        // 刻线的右缘对齐峰值位置，故左缘在 80 - 2。
        Assert.Equal(78, tick.X);
        Assert.Equal(2, tick.Width);
    }

    [Fact]
    public void LevelMeter_FullPeak_KeepsTheTickInsideTheControl()
    {
        var (_, tick) = SpectrumGeometry.LevelMeter(rms: 1, peak: 1, Width, Height);

        Assert.True(tick.Right <= Width, $"刻线越右界：{tick.Right}");
    }

    [Fact]
    public void LevelMeter_ZeroPeak_KeepsTheTickInsideTheControl()
    {
        var (_, tick) = SpectrumGeometry.LevelMeter(rms: 0, peak: 0, Width, Height);

        Assert.True(tick.X >= 0, $"刻线越左界：{tick.X}");
    }

    [Fact]
    public void LevelMeter_OutOfRangeValues_AreClamped()
    {
        var (fill, tick) = SpectrumGeometry.LevelMeter(rms: 3, peak: -1, Width, Height);

        Assert.InRange(fill.Width, 0, Width);
        Assert.InRange(tick.X, 0, Width);
    }

    [Fact]
    public void LevelMeter_PeakBelowRms_DoesNotThrow()
    {
        // 分析器保证 peak >= rms，但几何层不假设上游正确——
        // 这一层的职责只是把数映射成矩形。
        var (fill, tick) = SpectrumGeometry.LevelMeter(rms: 0.9, peak: 0.1, Width, Height);

        Assert.True(fill.Width >= 0);
        Assert.True(tick.Width >= 0);
    }

    [Fact]
    public void LevelMeter_ZeroWidthControl_ProducesNoNegativeGeometry()
    {
        // 控件在布局完成前 Bounds 可能是 0。
        var (fill, tick) = SpectrumGeometry.LevelMeter(rms: 0.5, peak: 0.5, width: 0, Height);

        Assert.True(fill.Width >= 0);
        Assert.True(tick.Width >= 0);
        Assert.True(tick.X >= 0);
    }
}
