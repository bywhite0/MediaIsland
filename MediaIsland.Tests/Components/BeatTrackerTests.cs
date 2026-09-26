using MediaIsland.Controls;
using Xunit;

namespace MediaIsland.Tests.Components;

/// <summary>
/// 四拍计数的需求：按拍子稳定前进，而不是每个事件都跳。输入用平坦谱合成的脉冲序列——
/// 一拍抬高 3 帧（≈50ms），其余回落，足以代表「有鼓点的音乐」对包络的作用。
/// 真实曲目上的准确度由离线评估保证，这里锁的是行为契约。
/// </summary>
public class BeatTrackerTests
{
    private const int SampleRate = 48000;

    private static float[] Flat(float value) => Enumerable.Repeat(value, 1024).ToArray();

    /// <summary>在 [0, seconds) 上按给定帧间隔喂谱，返回报拍时刻。</summary>
    private static List<double> Run(
        BeatTracker tracker, Func<double, float[]> spectrumAt, double seconds,
        Func<double>? frameInterval = null, int sampleRate = SampleRate, double start = 0)
    {
        frameInterval ??= () => 1 / 60.0;
        var beats = new List<double>();
        for (var t = start; t < start + seconds; t += frameInterval())
        {
            if (tracker.Update(spectrumAt(t), sampleRate, t)) beats.Add(t);
        }

        return beats;
    }

    private static Func<double, float[]> Pulses(double bpm, double offset = 0.2) => t =>
    {
        var period = 60 / bpm;
        var phase = ((t - offset) % period + period) % period;
        return Flat(phase < 0.05 ? 0.6f : 0.05f);
    };

    /// <summary>报拍间隔的中值，以及每一拍到最近脉冲的相位误差（秒）。只看预热之后。</summary>
    private static (double MedianInterval, double MaxPhaseError) Measure(List<double> beats, double bpm, double offset = 0.2, double after = 5)
    {
        var steady = beats.Where(b => b >= after).ToList();
        Assert.True(steady.Count >= 4, $"预热后应持续报拍，实际 {steady.Count} 次");
        var intervals = steady.Zip(steady.Skip(1), (a, b) => b - a).OrderBy(x => x).ToList();
        var period = 60 / bpm;
        var maxError = steady.Max(b =>
        {
            var phase = ((b - offset) % period + period) % period;
            return Math.Min(phase, period - phase);
        });
        return (intervals[intervals.Count / 2], maxError);
    }

    [Theory]
    [InlineData(120)]
    [InlineData(180)]
    [InlineData(100)]
    public void SteadyPulses_AreCountedAtTheirTempo_AndInPhase(double bpm)
    {
        var tracker = new BeatTracker();
        var beats = Run(tracker, Pulses(bpm), 15);

        var (interval, phaseError) = Measure(beats, bpm);

        Assert.InRange(interval, 60 / bpm * 0.97, 60 / bpm * 1.03);
        Assert.True(phaseError < 0.05, $"报拍应落在脉冲上，最大相位误差 {phaseError * 1000:F0}ms");
        Assert.InRange(tracker.Bpm, bpm * 0.97, bpm * 1.03);
    }

    /// <summary>渲染表有抖动：包络按真实时间重采样，周期不随帧率漂移。</summary>
    [Fact]
    public void JitteredFrameTimes_StillLockToTheTempo()
    {
        var random = new Random(3);
        var beats = Run(new BeatTracker(), Pulses(128), 15, () => (14 + random.NextDouble() * 6) / 1000);

        var (interval, phaseError) = Measure(beats, 128);

        Assert.InRange(interval, 60 / 128.0 * 0.95, 60 / 128.0 * 1.05);
        Assert.True(phaseError < 0.06, $"最大相位误差 {phaseError * 1000:F0}ms");
    }

    /// <summary>频带边界按采样率换算；44.1kHz 端点不该失灵。</summary>
    [Fact]
    public void Works_At441kHz()
    {
        var tracker = new BeatTracker();
        var beats = Run(tracker, Pulses(120), 15, sampleRate: 44100);

        Assert.InRange(Measure(beats, 120).MedianInterval, 0.485, 0.515);
    }

    [Fact]
    public void NoBeats_DuringWarmUp_ThenStartsWithinFourSeconds()
    {
        var beats = Run(new BeatTracker(), Pulses(120), 8);

        Assert.DoesNotContain(beats, b => b < 3);
        Assert.Contains(beats, b => b < 4);
    }

    [Fact]
    public void Silence_NeverBeats()
    {
        var tracker = new BeatTracker();

        Assert.Empty(Run(tracker, _ => Flat(0), 15));
        Assert.Empty(Run(new BeatTracker(), _ => [], 15));
    }

    [Fact]
    public void SteadyTone_WithoutRhythm_NeverBeats()
    {
        Assert.Empty(Run(new BeatTracker(), _ => Flat(0.3f), 15));
    }

    /// <summary>暂停后窗口里仍存着旧节奏；不加活动门的话会对着静音继续数好几秒。</summary>
    [Fact]
    public void StopsCounting_SoonAfterTheMusicStops()
    {
        var pulses = Pulses(120);
        var beats = Run(new BeatTracker(), t => t < 10 ? pulses(t) : Flat(0), 16);

        Assert.Contains(beats, b => b > 8 && b < 10);
        Assert.DoesNotContain(beats, b => b > 11.5);
    }

    [Fact]
    public void Reset_StartsOverWithAFreshWarmUp()
    {
        var tracker = new BeatTracker();
        Run(tracker, Pulses(120), 10);

        tracker.Reset();

        Assert.Equal(0, tracker.Bpm);
        var after = Run(tracker, Pulses(120), 5, start: 10);
        Assert.DoesNotContain(after, b => b < 13);
    }

    /// <summary>长时间没有新谱（帧流中断）后旧包络已无意义，按重新开播处理。</summary>
    [Fact]
    public void LongGap_RestartsTheWarmUp()
    {
        var tracker = new BeatTracker();
        Run(tracker, Pulses(120), 10);

        var after = Run(tracker, Pulses(120), 5, start: 30);

        Assert.DoesNotContain(after, b => b < 33);
    }
}
