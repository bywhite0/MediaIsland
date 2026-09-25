using MediaIsland.Services.Audio.Playback;
using Xunit;

namespace MediaIsland.Tests.RealDevice;

public class AlignmentFollowToleranceTests
{
    [Theory]
    [InlineData(48000, 480, 10000, 45)]
    [InlineData(96000, 960, 10000, 45)]
    [InlineData(44100, 441, 10000, 45)]
    [InlineData(48000, 480, 60000, 95)]
    [InlineData(48000, 480, 0, 35)]
    public void Calculate_UsesEndpointRateBufferAndTail(int rate, long buffer, long tail, double expected)
    {
        Assert.Equal(expected, AlignmentFollowTolerance.Calculate(Stats(rate, buffer, tail)));
    }

    [Theory]
    [InlineData(0, 480, 10000)]
    [InlineData(-1, 480, 10000)]
    [InlineData(48000, 0, 10000)]
    [InlineData(48000, -1, 10000)]
    [InlineData(48000, 480, -1)]
    public void Calculate_RejectsInvalidEndpointFacts(int rate, long buffer, long tail)
    {
        Assert.Throws<ArgumentOutOfRangeException>("stats",
            () => AlignmentFollowTolerance.Calculate(Stats(rate, buffer, tail)));
    }

    [Theory]
    [InlineData(44.999, true)]
    [InlineData(45, false)]
    [InlineData(45.001, false)]
    public void IsDiscriminating_RequiresToleranceStrictlyBelowStep(double tolerance, bool expected)
    {
        Assert.Equal(expected, AlignmentFollowTolerance.IsDiscriminating(tolerance, 45));
    }

    [Fact]
    public void Summarize_UsesPairedAbsoluteDifferencesNotDifferenceOfMedians()
    {
        var summary = AlignmentFollowTolerance.Summarize([(100, 130), (200, 100), (300, 210)]);
        Assert.Equal(130, summary.RingMedianMs);
        Assert.Equal(90, summary.DifferenceMedianMs);
        Assert.Equal(100, summary.DifferenceMaxMs);
        Assert.Equal(3, summary.Count);
    }

    [Fact]
    public void Summarize_DoesNotCancelOppositeSignedDifferences()
    {
        var summary = AlignmentFollowTolerance.Summarize([(70, 100), (100, 100), (200, 100)]);
        Assert.Equal(30, summary.DifferenceMedianMs);
        Assert.Equal(100, summary.DifferenceMaxMs);
    }

    [Fact]
    public void Summarize_EvenSamplesUseTheUpperMedian()
    {
        var summary = AlignmentFollowTolerance.Summarize([(100, 100), (170, 110)]);
        Assert.Equal(110, summary.RingMedianMs);
        Assert.Equal(60, summary.DifferenceMedianMs);
        Assert.False(AlignmentFollowTolerance.IsFollowing(summary, 59.999));
        Assert.True(AlignmentFollowTolerance.IsFollowing(summary, 60));
    }

    [Fact]
    public void Summarize_RejectsEmptySamples()
    {
        Assert.Throws<ArgumentException>(() => AlignmentFollowTolerance.Summarize([]));
    }

    [Theory]
    [InlineData(double.NaN, 100)]
    [InlineData(100, double.NaN)]
    [InlineData(double.PositiveInfinity, 100)]
    [InlineData(100, double.NegativeInfinity)]
    [InlineData(-1, 100)]
    [InlineData(100, -1)]
    public void Summarize_RejectsInvalidSamples(double latency, double ring)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AlignmentFollowTolerance.Summarize([(latency, ring)]));
    }

    [Theory]
    [InlineData(300)]
    [InlineData(700)]
    public void IsFollowing_RejectsWholeStepMismatch(double step)
    {
        var tolerance = AlignmentFollowTolerance.Calculate(Stats(48000, 480, 10000));
        var matching = AlignmentFollowTolerance.Summarize([(220, 200), (221, 201), (219, 199)]);
        var shifted = AlignmentFollowTolerance.Summarize([(220 + step, 200), (221 + step, 201), (219 + step, 199)]);
        Assert.True(AlignmentFollowTolerance.IsFollowing(matching, tolerance));
        Assert.False(AlignmentFollowTolerance.IsFollowing(shifted, tolerance));
    }

    private static AudioRenderStats Stats(int rate, long buffer, long tail) =>
        new(0, 0, 0, 0, rate, 0, DeviceBufferFrames: buffer, DeviceLatencyUs: tail);
}

internal readonly record struct AlignmentFollowSummary(
    double RingMedianMs, double DifferenceMedianMs, double DifferenceMaxMs, int Count);

internal static class AlignmentFollowTolerance
{
    // 端点相关容差，不是任意调度、发送内容相位或非原子采样下的完整误差界。
    internal static double Calculate(AudioRenderStats stats)
    {
        if (stats.DeviceSampleRate <= 0 || stats.DeviceBufferFrames <= 0 || stats.DeviceLatencyUs < 0)
            throw new ArgumentOutOfRangeException(nameof(stats));

        return OutputLatencyTracker.DeadbandMs + stats.DeviceLatencyUs / 1000.0
            + stats.DeviceBufferFrames * 1000.0 / stats.DeviceSampleRate;
    }

    internal static bool IsDiscriminating(double toleranceMs, double stepMs) => toleranceMs < stepMs;

    internal static AlignmentFollowSummary Summarize(IReadOnlyList<(double LatencyMs, double RingMs)> samples)
    {
        if (samples.Count == 0)
            throw new ArgumentException("配对采样窗不能为空", nameof(samples));
        if (samples.Any(p => !double.IsFinite(p.LatencyMs) || !double.IsFinite(p.RingMs)
            || p.LatencyMs < 0 || p.RingMs < 0))
            throw new ArgumentOutOfRangeException(nameof(samples));

        var rings = samples.Select(p => p.RingMs).Order().ToArray();
        var differences = samples.Select(p => Math.Abs(p.LatencyMs - p.RingMs)).Order().ToArray();
        return new(rings[rings.Length / 2], differences[differences.Length / 2], differences[^1], samples.Count);
    }

    internal static bool IsFollowing(AlignmentFollowSummary summary, double toleranceMs) =>
        summary.DifferenceMedianMs <= toleranceMs;
}
