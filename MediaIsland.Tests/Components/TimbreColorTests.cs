using Avalonia.Media;
using MediaIsland.Controls;
using Xunit;

namespace MediaIsland.Tests.Components;

public class TimbreColorTests
{
    private static readonly Color Accent = Color.FromRgb(218, 100, 95);

    [Fact]
    public void NeutralCentroid_IsTheBaseColor()
    {
        Assert.Equal(Accent, TimbreColor.Mix(Accent, 0.5));
    }

    [Fact]
    public void BrightSound_ShiftsTowardWarm_DarkSoundTowardPurple()
    {
        var bright = TimbreColor.Mix(Accent, 1);
        var dark = TimbreColor.Mix(Accent, 0);

        Assert.True(bright.G > Accent.G, "亮音色应偏暖黄（绿分量上升）");
        Assert.True(dark.B > Accent.B, "暗音色应偏紫（蓝分量上升）");
    }

    [Theory]
    [InlineData(-3)]
    [InlineData(7)]
    [InlineData(double.NaN)]
    public void OutOfRangeCentroid_IsClampedWithoutThrowing(double centroid)
    {
        var color = TimbreColor.Mix(Accent, centroid);

        Assert.Equal(Accent.A, color.A);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.49)]
    [InlineData(1)]
    public void TranslucentBase_KeepsItsAlphaAcrossTheWholeRange(double centroid)
    {
        var translucent = Color.FromArgb(128, 218, 100, 95);

        Assert.Equal(128, TimbreColor.Mix(translucent, centroid).A);
    }
}
