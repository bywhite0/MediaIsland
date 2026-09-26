using Avalonia;
using MediaIsland.Components;
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

    /// <summary>
    /// 「段数 × 间距 ≥ 宽度」时，间距若照原值扣，每根都夹到 0 宽，有声无声都一片空白。
    /// 间距最多吃掉半个槽，图元始终有宽度。两组取值是细柱默认 16 段拖窄、和 64 段的实际配置。
    /// </summary>
    [Theory]
    [InlineData(16, 32)]
    [InlineData(64, 96)]
    public void NarrowWidthOrManyBands_EveryShapeKeepsAPositiveWidth(int count, double width)
    {
        var values = Enumerable.Repeat(0.5f, count).ToArray();

        Assert.All(SpectrumGeometry.Bars(values, width, Height, gap: 2, mirrored: false),
            r => Assert.True(r.Width > 0, $"Bars 宽度为 {r.Width}"));
        Assert.All(SpectrumGeometry.Capsules(values, width, Height),
            r => Assert.True(r.Width > 0, $"Capsules 宽度为 {r.Width}"));
        Assert.All(SpectrumGeometry.PeakCaps(values, width, Height, 2, mirrored: false),
            r => Assert.True(r.Width > 0, $"PeakCaps 宽度为 {r.Width}"));
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

    // ---- 每种样式都要有静止形态 ----

    public static TheoryData<SpectrumStyle> AllStyles()
    {
        var data = new TheoryData<SpectrumStyle>();
        foreach (var style in Enum.GetValues<SpectrumStyle>()) data.Add(style);
        return data;
    }

    /// <summary>
    /// 遍历全部样式枚举值，而不是逐个手写：新增样式忘了静止形态时这里会自动变红。
    /// 「非空」的判据按图元种类各取其一：矩形有正面积、点列不少于两点、圆有正半径。
    /// </summary>
    [Theory]
    [MemberData(nameof(AllStyles))]
    public void EveryStyle_AtZeroEnergy_StillDrawsSomething(SpectrumStyle style)
    {
        var zeros = new float[7];
        var drawn = style switch
        {
            SpectrumStyle.Capsule => SpectrumGeometry.Capsules(zeros, Width, Height).Any(r => r.Width > 0 && r.Height > 0),
            SpectrumStyle.PeakCap => SpectrumGeometry.PeakCaps(zeros, Width, Height, 2, false).Any(r => r.Width > 0 && r.Height > 0),
            SpectrumStyle.Ridge => SpectrumGeometry.Ridge(zeros, Width, Height, false).Length >= 2,
            SpectrumStyle.Spectrogram => SpectrumGeometry.SpectrogramCells([], SpectrumGeometry.SpectrogramColumns, Width, Height).Any(c => c.Cell.Width > 0),
            SpectrumStyle.Tri => SpectrumGeometry.Tri(0, 0, 0, Width, Height).All(c => c.Radius > 0),
            SpectrumStyle.Chroma => SpectrumGeometry.Chroma(new float[12], Width, Height).Arcs.Length == 12,
            _ => throw new ArgumentOutOfRangeException(nameof(style))
        };

        Assert.True(drawn, $"{style} 在零能量时什么都没画");
    }

    [Theory]
    [MemberData(nameof(AllStyles))]
    public void EveryStyle_ZeroSizeOrGarbageInput_DoesNotThrow(SpectrumStyle style)
    {
        float[] garbage = [float.NaN, -1f, 5f];
        foreach (var (w, h) in new[] { (0d, 0d), (Width, Height) })
        {
            _ = style switch
            {
                SpectrumStyle.Capsule => (object)SpectrumGeometry.Capsules(garbage, w, h),
                SpectrumStyle.PeakCap => SpectrumGeometry.PeakCaps(garbage, w, h, 2, true),
                SpectrumStyle.Ridge => SpectrumGeometry.Ridge(garbage, w, h, true),
                SpectrumStyle.Spectrogram => SpectrumGeometry.SpectrogramCells([garbage], 40, w, h),
                SpectrumStyle.Tri => SpectrumGeometry.Tri(float.NaN, -1, 5, w, h),
                SpectrumStyle.Chroma => SpectrumGeometry.Chroma(garbage, w, h),
                _ => throw new ArgumentOutOfRangeException(nameof(style))
            };
        }
    }

    // ---- Capsules ----

    [Fact]
    public void Capsules_AreSymmetricAboutTheHorizontalCenter()
    {
        var capsules = SpectrumGeometry.Capsules([0.5f, 1f], Width, Height);

        Assert.All(capsules, c => Assert.Equal(Height / 2, c.Center.Y, 6));
        Assert.Equal(Height, capsules[1].Height, 6);
    }

    [Fact]
    public void Capsules_AtZero_AreDotsAsTallAsTheyAreWide()
    {
        var capsules = SpectrumGeometry.Capsules([0f, 0f, 0f], Width, Height);

        Assert.All(capsules, c => Assert.Equal(c.Width, c.Height, 6));
    }

    [Fact]
    public void Capsules_AreExactlyHalfTheirSlotWide_CenteredInTheSlot()
    {
        var capsules = SpectrumGeometry.Capsules([0.5f, 0.5f], Width, Height);

        var slot = Width / 2;
        for (var i = 0; i < capsules.Length; i++)
        {
            Assert.Equal(slot / 2, capsules[i].Width, 6);
            Assert.Equal(slot * i + slot / 2, capsules[i].Center.X, 6);
        }
    }

    // ---- PeakCaps ----

    [Fact]
    public void PeakCaps_SitAtThePeakAndStayInside()
    {
        var caps = SpectrumGeometry.PeakCaps([0f, 0.5f, 1f], Width, Height, 2, mirrored: false);

        Assert.Equal(Height - SpectrumGeometry.PeakCapThickness, caps[0].Y, 6);
        Assert.Equal(Height * 0.5 - SpectrumGeometry.PeakCapThickness, caps[1].Y, 6);
        Assert.Equal(0, caps[2].Y, 6);
    }

    [Fact]
    public void PeakCaps_Mirrored_ProducesATopAndBottomCapPerBand()
    {
        var caps = SpectrumGeometry.PeakCaps([0.5f, 0.5f], Width, Height, 2, mirrored: true);

        Assert.Equal(4, caps.Length);
        Assert.Equal(Height / 2, (caps[0].Bottom + caps[1].Y) / 2, 6);
    }

    // ---- Ridge ----

    [Fact]
    public void Ridge_SpansTheFullWidth_AndRisesWithValue()
    {
        var points = SpectrumGeometry.Ridge([0f, 1f, 0f], Width, Height, mirrored: false);

        Assert.Equal(0, points[0].X);
        Assert.Equal(Width, points[^1].X, 6);
        Assert.True(points[1].Y < points[0].Y);
    }

    [Fact]
    public void Ridge_AtZero_SitsOnTheBaseline()
    {
        var points = SpectrumGeometry.Ridge([0f, 0f], Width, Height, mirrored: false);

        Assert.All(points, p => Assert.Equal(Height - SpectrumGeometry.RidgeBaseline, p.Y, 6));
    }

    [Fact]
    public void Ridge_Mirrored_StaysInTheUpperHalf()
    {
        var points = SpectrumGeometry.Ridge([1f, 0.5f, 0f], Width, Height, mirrored: true);

        Assert.All(points, p => Assert.InRange(p.Y, 0, Height / 2));
    }

    // ---- Spectrogram ----

    [Fact]
    public void SpectrogramCells_AlwaysFillTheWholeGrid_NewestOnTheRight()
    {
        var cells = SpectrumGeometry.SpectrogramCells(
            [Enumerable.Repeat(1f, 6).ToArray()], SpectrumGeometry.SpectrogramColumns, Width, Height);

        Assert.Equal(SpectrumGeometry.SpectrogramRows * SpectrumGeometry.SpectrogramColumns, cells.Length);
        var lit = cells.Where(c => c.Value > 0).ToArray();
        Assert.Equal(6, lit.Length);
        Assert.All(lit, c => Assert.True(c.Cell.Right > Width - Width / SpectrumGeometry.SpectrogramColumns - 1e-9));
    }

    [Fact]
    public void SpectrogramCells_LowFrequencyRowIsAtTheBottom()
    {
        float[] onlyLow = [1f, 0, 0, 0, 0, 0];
        var cells = SpectrumGeometry.SpectrogramCells([onlyLow], 40, Width, Height);

        var lit = Assert.Single(cells, c => c.Value > 0);
        Assert.True(lit.Cell.Bottom >= Height - 1e-9 - 1);
    }

    // ---- Tri ----

    [Fact]
    public void Tri_LaysOutLowMidHighLeftToRight_AndGrowsWithEnergy()
    {
        var dots = SpectrumGeometry.Tri(1f, 0f, 0.5f, Width, Height);

        Assert.True(dots[0].Center.X < dots[1].Center.X && dots[1].Center.X < dots[2].Center.X);
        Assert.True(dots[0].Radius > dots[2].Radius && dots[2].Radius > dots[1].Radius);
    }

    // ---- Chroma ----

    [Fact]
    public void Chroma_TwelveArcsStartingAtTheTop_CarryTheirValues()
    {
        var chroma = new float[12];
        chroma[9] = 1f;

        var ring = SpectrumGeometry.Chroma(chroma, Width, Height);

        Assert.Equal(12, ring.Arcs.Length);
        // 第 0 段从正上方（-90°）起，让出 3.5° 的半个段间空隙。
        Assert.Equal(-90 + 3.5, ring.Arcs[0].StartDeg, 6);
        Assert.Equal(1f, ring.Arcs[9].Value);
        Assert.True(ring.Radius <= Math.Min(Width, Height) / 2);
    }
}
