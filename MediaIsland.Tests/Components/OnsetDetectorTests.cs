using MediaIsland.Controls;
using Xunit;

namespace MediaIsland.Tests.Components;

public class OnsetDetectorTests
{
    private const double Frame = 1 / 60.0;

    private static float[] Flat(float value) => Enumerable.Repeat(value, 64).ToArray();

    private static int CountOnsets(OnsetDetector detector, Func<int, float[]> spectrumAt, int frames)
    {
        var count = 0;
        for (var i = 0; i < frames; i++)
        {
            if (detector.Update(spectrumAt(i), i * Frame)) count++;
        }

        return count;
    }

    [Fact]
    public void Step_IsDetectedOnce()
    {
        var onsets = CountOnsets(new OnsetDetector(), i => i < 60 ? Flat(0.05f) : Flat(0.8f), 120);

        Assert.Equal(1, onsets);
    }

    [Fact]
    public void SteadyTone_IsNeverAnOnset()
    {
        var detector = new OnsetDetector();
        // 前 60 帧让滑动均值就位；稳态从第一帧起就是同一个谱。
        var onsets = CountOnsets(detector, _ => Flat(0.5f), 180);

        Assert.True(onsets <= 1, $"稳态至多在第一帧被当作起拍一次，实际 {onsets}");
        Assert.False(detector.Update(Flat(0.5f), 181 * Frame));
    }

    [Fact]
    public void Silence_IsNeverAnOnset()
    {
        Assert.Equal(0, CountOnsets(new OnsetDetector(), _ => Flat(0f), 120));
    }

    [Fact]
    public void TwoStepsWithin120ms_CountOnce()
    {
        // 第 60 帧起跳，第 63 帧（50ms 后）再跳一级。
        var onsets = CountOnsets(
            new OnsetDetector(),
            i => i < 60 ? Flat(0.05f) : i < 63 ? Flat(0.5f) : Flat(1f),
            90);

        Assert.Equal(1, onsets);
    }

    [Fact]
    public void Reset_ForgetsThePreviousSpectrum()
    {
        // 不 Reset 时 0 → 0.8 的通量必然起拍；Reset 后这一帧没有「上一帧」，通量按 0 计。
        var detector = new OnsetDetector();
        CountOnsets(detector, _ => Flat(0f), 60);

        detector.Reset();

        Assert.False(detector.Update(Flat(0.8f), 0));
    }

    [Fact]
    public void Reset_ForgetsTheLastOnsetTime()
    {
        // 第 60 帧（t = 1.0）起拍后 Reset，时间从 0 重新开始：不重置上次起拍时刻的话，
        // 第 3 帧的阶跃会因「距上次起拍不足 120ms」被吞掉。
        var detector = new OnsetDetector();
        Assert.Equal(1, CountOnsets(detector, i => i < 60 ? Flat(0.05f) : Flat(0.8f), 61));

        detector.Reset();

        Assert.Equal(1, CountOnsets(detector, i => i < 3 ? Flat(0.05f) : Flat(0.8f), 4));
    }
}
