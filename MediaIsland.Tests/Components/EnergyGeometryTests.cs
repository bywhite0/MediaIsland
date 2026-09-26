using Avalonia;
using MediaIsland.Components;
using MediaIsland.Controls;
using Xunit;

namespace MediaIsland.Tests.Components;

public class EnergyGeometryTests
{
    private const double W = 48;
    private const double H = 32;

    public static TheoryData<EnergyStyle> AllStyles()
    {
        var data = new TheoryData<EnergyStyle>();
        foreach (var s in Enum.GetValues<EnergyStyle>()) data.Add(s);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllStyles))]
    public void EveryStyle_AtZeroEnergy_StillDrawsSomething(EnergyStyle style)
    {
        var drawn = style switch
        {
            EnergyStyle.Orb => EnergyGeometry.Orb(0, W, H).Radius > 0,
            EnergyStyle.Ripple => EnergyGeometry.Ripple(0, [], W, H).CoreRadius > 0,
            EnergyStyle.GlowLine => EnergyGeometry.GlowLine(0, W, H) is var g && g.Line.Height > 0 && g.Opacity > 0,
            EnergyStyle.Dots => EnergyGeometry.Dots(0, 0, W, H).Radius > 0,
            EnergyStyle.History => EnergyGeometry.History([], EnergyGeometry.HistoryCapacity, W, H).Length >= 2,
            EnergyStyle.Beat => EnergyGeometry.Beat(W, H).Radius > 0,
            _ => throw new ArgumentOutOfRangeException(nameof(style))
        };

        Assert.True(drawn, $"{style} 在零能量时什么都没画");
    }

    [Theory]
    [MemberData(nameof(AllStyles))]
    public void EveryStyle_ZeroSizeOrGarbageInput_DoesNotThrow(EnergyStyle style)
    {
        foreach (var (w, h) in new[] { (0d, 0d), (W, H) })
        foreach (var e in new[] { double.NaN, -1, 5 })
        {
            _ = style switch
            {
                EnergyStyle.Orb => (object)EnergyGeometry.Orb(e, w, h),
                EnergyStyle.Ripple => EnergyGeometry.Ripple(e, [new RippleRing(e, e)], w, h),
                EnergyStyle.GlowLine => EnergyGeometry.GlowLine(e, w, h),
                EnergyStyle.Dots => EnergyGeometry.Dots(e, e, w, h),
                EnergyStyle.History => EnergyGeometry.History([(float)e], 180, w, h),
                EnergyStyle.Beat => EnergyGeometry.Beat(w, h),
                _ => throw new ArgumentOutOfRangeException(nameof(style))
            };
        }
    }

    [Fact]
    public void Orb_GrowsWithEnergy_AndStaysCentered()
    {
        var quiet = EnergyGeometry.Orb(0, W, H);
        var loud = EnergyGeometry.Orb(1, W, H);

        Assert.Equal(new Point(W / 2, H / 2), loud.Center);
        Assert.True(loud.Radius > quiet.Radius);
        Assert.Equal(SpectrumGeometry.MinDot, quiet.Radius);
        Assert.True(loud.Radius <= H / 2);
        Assert.True(loud.GlowRadius > loud.Radius);
    }

    [Fact]
    public void Ripple_RingsExpandAndFadeWithAge()
    {
        var (_, _, rings) = EnergyGeometry.Ripple(0.5, [new RippleRing(0.1, 1), new RippleRing(0.9, 1)], W, H);

        Assert.True(rings[1].Radius > rings[0].Radius);
        Assert.True(rings[1].Opacity < rings[0].Opacity);
    }

    [Fact]
    public void GlowLine_ThickensAndBrightensWithEnergy()
    {
        var quiet = EnergyGeometry.GlowLine(0, W, H);
        var loud = EnergyGeometry.GlowLine(1, W, H);

        Assert.Equal(W, loud.Line.Width);
        Assert.True(loud.Line.Height > quiet.Line.Height);
        Assert.True(loud.Opacity > quiet.Opacity);
        Assert.Equal(H / 2, loud.Line.Center.Y, 6);
    }

    [Fact]
    public void Dots_AtZero_LieOnTheCenterLine()
    {
        var (centers, _) = EnergyGeometry.Dots(0, 1.234, W, H);

        Assert.Equal(3, centers.Length);
        Assert.All(centers, c => Assert.Equal(H / 2, c.Y, 6));
    }

    [Fact]
    public void Dots_StayInsideTheControl_AtFullEnergy()
    {
        for (var t = 0.0; t < 2; t += 0.05)
        {
            var (centers, r) = EnergyGeometry.Dots(1, t, W, H);
            Assert.All(centers, c => Assert.InRange(c.Y - r, -1e-9, H));
        }
    }

    [Fact]
    public void History_NewestIsOnTheRightEdge()
    {
        var points = EnergyGeometry.History([0f, 1f], 180, W, H);

        Assert.Equal(W, points[^1].X, 6);
        Assert.True(points[^1].Y < points[^2].Y);
    }

    [Fact]
    public void Beat_FourDotsEvenlySpacedAcrossTheCenter()
    {
        var (centers, _) = EnergyGeometry.Beat(W, H);

        Assert.Equal(4, centers.Length);
        Assert.Equal(centers[1].X - centers[0].X, centers[3].X - centers[2].X, 6);
        Assert.Equal(W / 2, (centers[0].X + centers[3].X) / 2, 6);
    }
}
