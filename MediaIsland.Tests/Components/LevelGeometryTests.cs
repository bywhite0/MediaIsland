using Avalonia;
using MediaIsland.Components;
using MediaIsland.Controls;
using Xunit;

namespace MediaIsland.Tests.Components;

/// <summary>
/// 音频电平的几何。电平表用例从 SpectrumGeometryTests 迁来；底轨与声像光点是新增的——
/// 零电平时仍要画出东西，否则静止形态下组件看起来像没加载。
/// </summary>
public class LevelGeometryTests
{
    private const double Width = 100;
    private const double Height = 40;

    // ---- Meter ----

    [Fact]
    public void LevelMeter_FillsProportionallyFromTheLeft()
    {
        var (_, fill, _) = LevelGeometry.Meter(rms: 0.5, peak: 0.8, Width, Height);

        Assert.Equal(50, fill.Width);
        Assert.Equal(0, fill.X);
        Assert.Equal(Height, fill.Height);
    }

    [Fact]
    public void LevelMeter_PeakTickTrailsThePeakPosition()
    {
        var (_, _, tick) = LevelGeometry.Meter(rms: 0.5, peak: 0.8, Width, Height);

        // 刻线的右缘对齐峰值位置，故左缘在 80 - 2。
        Assert.Equal(78, tick.X);
        Assert.Equal(2, tick.Width);
    }

    [Fact]
    public void LevelMeter_FullPeak_KeepsTheTickInsideTheControl()
    {
        var (_, _, tick) = LevelGeometry.Meter(rms: 1, peak: 1, Width, Height);

        Assert.True(tick.Right <= Width, $"刻线越右界：{tick.Right}");
    }

    [Fact]
    public void LevelMeter_ZeroPeak_KeepsTheTickInsideTheControl()
    {
        var (_, _, tick) = LevelGeometry.Meter(rms: 0, peak: 0, Width, Height);

        Assert.True(tick.X >= 0, $"刻线越左界：{tick.X}");
    }

    [Fact]
    public void LevelMeter_OutOfRangeValues_AreClamped()
    {
        var (_, fill, tick) = LevelGeometry.Meter(rms: 3, peak: -1, Width, Height);

        Assert.InRange(fill.Width, 0, Width);
        Assert.InRange(tick.X, 0, Width);
    }

    [Fact]
    public void LevelMeter_PeakBelowRms_DoesNotThrow()
    {
        // 分析器保证 peak >= rms，但几何层不假设上游正确——
        // 这一层的职责只是把数映射成矩形。
        var (_, fill, tick) = LevelGeometry.Meter(rms: 0.9, peak: 0.1, Width, Height);

        Assert.True(fill.Width >= 0);
        Assert.True(tick.Width >= 0);
    }

    [Fact]
    public void LevelMeter_ZeroWidthControl_ProducesNoNegativeGeometry()
    {
        // 控件在布局完成前 Bounds 可能是 0。
        var (_, fill, tick) = LevelGeometry.Meter(rms: 0.5, peak: 0.5, width: 0, Height);

        Assert.True(fill.Width >= 0);
        Assert.True(tick.Width >= 0);
        Assert.True(tick.X >= 0);
    }

    [Fact]
    public void Meter_TrackSpansTheWholeControl()
    {
        var (track, _, _) = LevelGeometry.Meter(0.3, 0.5, Width, Height);

        Assert.Equal(new Rect(0, 0, Width, Height), track);
    }

    [Fact]
    public void Meter_AtZero_DrawsOnlyTheTrack()
    {
        // 静止形态只剩底轨：零峰值时的刻线会落在底轨左端圆角外，像一条游离的竖线。
        var (_, fill, tick) = LevelGeometry.Meter(0, 0, Width, Height);

        Assert.Equal(0, fill.Width);
        Assert.Equal(0, tick.Width);
    }

    // ---- Pan ----

    [Fact]
    public void Pan_LeftOnly_SitsLeftOfCenter_RightOnly_RightOfCenter()
    {
        var left = LevelGeometry.Pan(1, 0, Width, Height);
        var right = LevelGeometry.Pan(0, 1, Width, Height);

        Assert.True(left.Dot.X < Width / 2);
        Assert.True(right.Dot.X > Width / 2);
        Assert.Equal(Width / 2, (left.Dot.X + right.Dot.X) / 2, 6);
    }

    [Theory]
    [InlineData(0.5, 0.5)]
    [InlineData(0, 0)]
    [InlineData(double.NaN, double.NaN)]
    public void Pan_EqualOrSilentChannels_SitsAtTheCenter(double left, double right)
    {
        var pan = LevelGeometry.Pan(left, right, Width, Height);

        Assert.Equal(Width / 2, pan.Dot.X, 6);
        Assert.False(double.IsNaN(pan.Radius));
    }

    [Fact]
    public void Pan_NearSilenceBelowTheIdleThreshold_SitsAtTheCenter()
    {
        // 两声道都低于停表阈值（0.001）时组件会停表；光点若按底噪比例偏移，就会冻结在一侧。
        var pan = LevelGeometry.Pan(0.0008, 0.0002, Width, Height);

        Assert.Equal(Width / 2, pan.Dot.X, 1e-6);
    }

    [Fact]
    public void Pan_DotGrowsWithTheLouderChannel_AndStaysInside()
    {
        var quiet = LevelGeometry.Pan(0.1, 0.1, Width, Height);
        var loud = LevelGeometry.Pan(1, 0, Width, Height);

        Assert.True(loud.Radius > quiet.Radius);
        Assert.True(loud.Dot.X - loud.Radius >= -1e-9);
    }

    // ---- 每种样式都要有静止形态 ----

    public static TheoryData<LevelStyle> AllStyles()
    {
        var data = new TheoryData<LevelStyle>();
        foreach (var s in Enum.GetValues<LevelStyle>()) data.Add(s);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllStyles))]
    public void EveryStyle_AtZero_StillDrawsSomething(LevelStyle style)
    {
        var drawn = style switch
        {
            LevelStyle.Meter => LevelGeometry.Meter(0, 0, Width, Height).Track is var t && t.Width > 0 && t.Height > 0,
            LevelStyle.Pan => LevelGeometry.Pan(0, 0, Width, Height).Radius > 0,
            _ => throw new ArgumentOutOfRangeException(nameof(style))
        };

        Assert.True(drawn, $"{style} 在零电平时什么都没画");
    }
}
