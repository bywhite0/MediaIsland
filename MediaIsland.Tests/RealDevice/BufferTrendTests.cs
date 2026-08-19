using Xunit;

namespace MediaIsland.Tests.RealDevice;

/// <summary>
/// 占用斜率的判据。这个函数是漂移判定的全部依据，它算错会让真机套件给出一个
/// 看起来精确的错误结论。不需要真机，故不带门控。
/// </summary>
public class BufferTrendTests
{
    [Fact]
    public void SteadyOccupancy_HasZeroSlope()
    {
        // 控制律正确（或注入与设备时钟自然匹配）时占用在目标附近振荡，斜率趋零。
        var samples = new List<(double, long)>();
        for (var i = 0; i < 20; i++)
        {
            // 围绕 9600 帧（200ms @ 48kHz）小幅振荡。
            samples.Add((i * 0.1, 9_600 + (i % 2 == 0 ? 120 : -120)));
        }

        var slope = BufferTrend.SlopeFramesPerSecond(samples);

        Assert.InRange(slope, -200, 200);
    }

    [Fact]
    public void SteadilyDrainingBuffer_HasNegativeSlope()
    {
        // 缓冲单调走向空的样子。每 100ms 掉 500 帧即 5000 帧每秒。
        var samples = new List<(double, long)>();
        for (var i = 0; i < 20; i++)
        {
            samples.Add((i * 0.1, 9_600 - i * 500L));
        }

        var slope = BufferTrend.SlopeFramesPerSecond(samples);

        Assert.InRange(slope, -5_500, -4_500);
    }

    [Fact]
    public void SteadilyFillingBuffer_HasPositiveSlope()
    {
        // 另一个方向。两条都要，否则把斜率算成绝对值也能让上一条通过。
        var samples = new List<(double, long)>();
        for (var i = 0; i < 20; i++)
        {
            samples.Add((i * 0.1, 9_600 + i * 500L));
        }

        var slope = BufferTrend.SlopeFramesPerSecond(samples);

        Assert.InRange(slope, 4_500, 5_500);
    }

    [Fact]
    public void TooFewSamples_YieldZeroInsteadOfThrowing()
    {
        // 真机测试里采样可能因为设备故障而一条都没拿到。那时该由调用方的「样本数」
        // 判据报错，不该由斜率函数抛异常把真正的失败原因盖掉。
        Assert.Equal(0, BufferTrend.SlopeFramesPerSecond([]));
        Assert.Equal(0, BufferTrend.SlopeFramesPerSecond([(0.0, 9_600L)]));
    }

    [Fact]
    public void IdenticalTimestamps_YieldZeroInsteadOfInfinity()
    {
        // 零方差的时间轴会让最小二乘的分母为零。Stopwatch 精度极高，实际不会发生，
        // 但除零的后果是 NaN，而 NaN 会静默通过 Math.Abs(x) < bound 这类判据。
        var slope = BufferTrend.SlopeFramesPerSecond([(1.0, 100L), (1.0, 200L)]);

        Assert.Equal(0, slope);
    }
}
