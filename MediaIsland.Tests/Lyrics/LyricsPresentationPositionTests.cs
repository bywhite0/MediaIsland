using MediaIsland.Services.Lyrics;
using Xunit;

namespace MediaIsland.Tests.Lyrics;

/// <summary>
/// 歌词呈现位置的合成。三个量各有自己的符号，而搞错符号的表现是「歌词整体偏了一点」——
/// 那恰好是最容易被当成主观感受而不是缺陷的一种失效，故必须有判据把符号钉死。
/// </summary>
public class LyricsPresentationPositionTests
{
    private static TimeSpan Ms(double value) => TimeSpan.FromMilliseconds(value);

    [Fact]
    public void NoOffsets_ReturnsClockUnchanged()
    {
        Assert.Equal(Ms(30_000), LyricsPresentationPosition.Compose(Ms(30_000), TimeSpan.Zero, TimeSpan.Zero));
    }

    [Fact]
    public void SourceOffset_IsAdded()
    {
        // 来源偏移的既有语义：正值让歌词提前。本次改动不得动它。
        Assert.Equal(Ms(30_250), LyricsPresentationPosition.Compose(Ms(30_000), Ms(250), TimeSpan.Zero));
    }

    [Fact]
    public void OutputLatency_IsSubtracted()
    {
        // 本机出声时歌词必须后移一个缓冲深度。符号反了会让偏差变成两倍而不是零，
        // 故这一条是整组判据里区分力最强的。
        Assert.Equal(Ms(29_800), LyricsPresentationPosition.Compose(Ms(30_000), TimeSpan.Zero, Ms(200)));
    }

    [Fact]
    public void SourceOffsetAndLatency_HaveOppositeSigns()
    {
        // 两者同时存在时不得互相抵消错方向：+250 与 -200 应得 +50。
        Assert.Equal(Ms(30_050), LyricsPresentationPosition.Compose(Ms(30_000), Ms(250), Ms(200)));
    }

    [Fact]
    public void NegativeSourceOffset_StillApplies()
    {
        Assert.Equal(Ms(29_550), LyricsPresentationPosition.Compose(Ms(30_000), Ms(-250), Ms(200)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(120)]
    [InlineData(199)]
    public void LatencyExceedingClock_ClampsToZeroInsteadOfGoingNegative(double clockMs)
    {
        // 起播头几百毫秒里 clock 小于缓冲深度，减出来是负值。负位置会让选行落到第一行
        // 之前，表现为开头不显示歌词——那是补偿自己引入的新缺陷。
        var position = LyricsPresentationPosition.Compose(Ms(clockMs), TimeSpan.Zero, Ms(200));

        Assert.Equal(TimeSpan.Zero, position);
        Assert.False(position < TimeSpan.Zero, "位置不得为负");
    }

    [Fact]
    public void ClampDoesNotSwallowLegitimatePositions()
    {
        // 钳位不得把刚好越过界的正常值也吃掉，否则上面那条与「恒返回零」无从区分。
        Assert.Equal(Ms(1), LyricsPresentationPosition.Compose(Ms(201), TimeSpan.Zero, Ms(200)));
    }
}
