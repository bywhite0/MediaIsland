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
        var detector = new OnsetDetector();
        CountOnsets(detector, _ => Flat(0.8f), 60);

        detector.Reset();

        Assert.Equal(0, CountOnsets(detector, _ => Flat(0f), 30));
    }
}
