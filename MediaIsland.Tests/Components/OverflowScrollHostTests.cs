using MediaIsland.Controls;
using Xunit;

namespace MediaIsland.Tests.Components;

/// <summary>
/// 覆盖 Issue #37 的溢出滚动决策与推进逻辑。
/// 这两个方法是纯函数，可以脱离 Avalonia UI 线程直接验证。
/// </summary>
public class OverflowScrollHostTests
{
    [Fact]
    public void ResolveOverflow_ReturnsPositiveWhenContentExceedsViewport()
    {
        Assert.Equal(60, OverflowScrollHost.ResolveOverflow(true, 200, 260));
    }

    [Fact]
    public void ResolveOverflow_ReturnsZeroWhenDisabled()
    {
        Assert.Equal(0, OverflowScrollHost.ResolveOverflow(false, 200, 260));
    }

    [Theory]
    [InlineData(200, 200)]   // 恰好放得下
    [InlineData(200, 150)]   // 内容更短
    [InlineData(200, 200.5)] // 亚像素溢出，不值得滚动
    public void ResolveOverflow_ReturnsZeroWhenNoMeaningfulOverflow(double viewport, double content)
    {
        Assert.Equal(0, OverflowScrollHost.ResolveOverflow(true, viewport, content));
    }

    /// <summary>
    /// 每行独立判定：标题超宽不应让并不超宽的艺术家行也进入滚动状态。
    /// </summary>
    [Fact]
    public void ResolveOverflow_IsIndependentPerLine()
    {
        const double viewport = 240;

        var titleOverflow = OverflowScrollHost.ResolveOverflow(true, viewport, 520);
        var artistOverflow = OverflowScrollHost.ResolveOverflow(true, viewport, 90);

        Assert.True(titleOverflow > 0);
        Assert.Equal(0, artistOverflow);
    }

    [Theory]
    [InlineData(0, 260)]                          // 尚未布局
    [InlineData(200, 0)]                          // 无内容
    [InlineData(-10, 260)]                        // 非法宽度
    [InlineData(double.PositiveInfinity, 260)]    // 不限制宽度时不滚动
    [InlineData(double.NaN, 260)]
    public void ResolveOverflow_ReturnsZeroForNonRenderableSizes(double viewport, double content)
    {
        Assert.Equal(0, OverflowScrollHost.ResolveOverflow(true, viewport, content));
    }

    [Fact]
    public void ResolveOverflow_ReturnsZeroWhenContentWidthIsNotFinite()
    {
        Assert.Equal(0, OverflowScrollHost.ResolveOverflow(true, 200, double.PositiveInfinity));
        Assert.Equal(0, OverflowScrollHost.ResolveOverflow(true, 200, double.NaN));
    }

    [Fact]
    public void Advance_HoldsPositionWhileDwelling()
    {
        var (offset, forward, dwell) = OverflowScrollHost.Advance(
            offset: 0,
            movingForward: true,
            dwellRemaining: TimeSpan.FromSeconds(1),
            overflow: 60,
            delta: TimeSpan.FromSeconds(0.2));

        Assert.Equal(0, offset);
        Assert.True(forward);
        Assert.Equal(TimeSpan.FromSeconds(0.8), dwell);
    }

    [Fact]
    public void Advance_MovesForwardAfterDwellExpires()
    {
        var (offset, forward, dwell) = OverflowScrollHost.Advance(
            offset: 0,
            movingForward: true,
            dwellRemaining: TimeSpan.Zero,
            overflow: 60,
            delta: TimeSpan.FromSeconds(1));

        Assert.Equal(24, offset); // SpeedPixelsPerSecond
        Assert.True(forward);
        Assert.Equal(TimeSpan.Zero, dwell);
    }

    [Fact]
    public void Advance_ClampsAtFarEndAndReversesDirection()
    {
        var (offset, forward, dwell) = OverflowScrollHost.Advance(
            offset: 55,
            movingForward: true,
            dwellRemaining: TimeSpan.Zero,
            overflow: 60,
            delta: TimeSpan.FromSeconds(1));

        Assert.Equal(60, offset);
        Assert.False(forward);
        Assert.True(dwell > TimeSpan.Zero);
    }

    [Fact]
    public void Advance_ClampsAtStartAndReversesDirection()
    {
        var (offset, forward, dwell) = OverflowScrollHost.Advance(
            offset: 5,
            movingForward: false,
            dwellRemaining: TimeSpan.Zero,
            overflow: 60,
            delta: TimeSpan.FromSeconds(1));

        Assert.Equal(0, offset);
        Assert.True(forward);
        Assert.True(dwell > TimeSpan.Zero);
    }

    [Fact]
    public void Advance_ResetsWhenOverflowDisappears()
    {
        var (offset, forward, dwell) = OverflowScrollHost.Advance(
            offset: 40,
            movingForward: false,
            dwellRemaining: TimeSpan.FromSeconds(1),
            overflow: 0,
            delta: TimeSpan.FromSeconds(0.1));

        Assert.Equal(0, offset);
        Assert.True(forward);
        Assert.Equal(TimeSpan.Zero, dwell);
    }

    [Fact]
    public void Advance_NeverLeavesTheScrollableRange()
    {
        const double overflow = 60;
        var offset = 0d;
        var forward = true;
        var dwell = TimeSpan.Zero;

        // 跑满约 20 秒（30fps），确认来回往返期间位移始终落在 [0, overflow]。
        for (var i = 0; i < 600; i++)
        {
            (offset, forward, dwell) = OverflowScrollHost.Advance(
                offset, forward, dwell, overflow, TimeSpan.FromSeconds(1.0 / 30));

            Assert.InRange(offset, 0, overflow);
        }
    }

    /// <summary>
    /// 两行溢出量不同（标题长、艺术家略长）时各自独立推进，互不影响进度。
    /// </summary>
    [Fact]
    public void Advance_KeepsSeparateProgressPerLine()
    {
        var titleOffset = 0d;
        var titleForward = true;
        var titleDwell = TimeSpan.Zero;

        var artistOffset = 0d;
        var artistForward = true;
        var artistDwell = TimeSpan.Zero;

        for (var i = 0; i < 90; i++)
        {
            (titleOffset, titleForward, titleDwell) = OverflowScrollHost.Advance(
                titleOffset, titleForward, titleDwell, 280, TimeSpan.FromSeconds(1.0 / 30));

            (artistOffset, artistForward, artistDwell) = OverflowScrollHost.Advance(
                artistOffset, artistForward, artistDwell, 10, TimeSpan.FromSeconds(1.0 / 30));
        }

        Assert.InRange(titleOffset, 0, 280);
        Assert.InRange(artistOffset, 0, 10);
        // 溢出小的一行早已走完并折返，长的一行仍在推进，说明进度互相独立。
        Assert.NotEqual(titleOffset, artistOffset);
    }
}
