using System.Buffers.Binary;
using MediaIsland.Controls;
using MediaIsland.Services.Audio;
using MediaIsland.Services.Audio.Visualization;
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

    /// <summary>
    /// 窄带新音（人声、合成器起一个音）不是鼓点：它在谱上是横线，打击乐掩码把它压掉。
    /// 同样的阶跃铺满全频带（上一条用例）才是鼓击。不做分离时换音也会触发，这是乱闪的主要来源。
    /// </summary>
    [Fact]
    public void NarrowbandNoteStarting_IsNotAnOnset()
    {
        static float[] Note(float value)
        {
            var spectrum = new float[1024];
            for (var i = 200; i < 204; i++) spectrum[i] = value;
            return spectrum;
        }

        var onsets = CountOnsets(new OnsetDetector(), i => i < 60 ? Note(0f) : Note(0.5f), 120);

        Assert.Equal(0, onsets);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.01)]
    public void PulseTrain_IsDetectedOncePerPulse_AtAnyLevel(double gain)
    {
        // 每 30 帧（0.5s）一拍：前 6 帧抬高，其余回落。错开半拍起始——第一帧没有上一帧，
        // 恰落在第一帧的拍按设计不计。共 8 拍。
        var onsets = CountOnsets(
            new OnsetDetector(),
            i => Flat((float)(gain * ((i + 15) % 30 < 6 ? 0.6 : 0.1))),
            240);

        Assert.Equal(8, onsets);
    }

    /// <summary>
    /// 走真实分析器：和弦铺底 + 每 0.5s 一记底鼓（含击槌的宽频瞬态），60fps 取谱。
    /// 平坦谱测不出通量的量级问题，只有真实谱能。多出的至多几次出现在开头，滑动均值尚未就位。
    /// </summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(0.02)]
    public void KickPattern_ThroughTheAnalyzer_IsDetectedAboutOncePerKick(double gain)
    {
        const int kicks = 10;
        var random = new Random(1);
        var onsets = CountThroughAnalyzer(kicks * 0.5, t =>
        {
            var sinceKick = t % 0.5;
            var kick = 0.6 * Math.Exp(-sinceKick / 0.08)
                       * Math.Sin(2 * Math.PI * (60 + 80 * Math.Exp(-sinceKick / 0.02)) * sinceKick);
            var click = sinceKick < 0.004 ? 0.4 * (random.NextDouble() * 2 - 1) : 0;
            var pad = 0.08 * (Math.Sin(2 * Math.PI * 220 * t) + Math.Sin(2 * Math.PI * 330 * t));
            var noise = 0.02 * (random.NextDouble() * 2 - 1);
            return gain * (kick + click + pad + noise);
        });

        Assert.InRange(onsets, kicks - 1, kicks + 3);
    }

    /// <summary>
    /// 没有鼓、只有和弦每 0.5s 换一次（30ms 起音与释音，像人声与铺底那样）：不该跟着换音闪。
    /// 起止必须平滑——幅度瞬间跳变本身就是一个宽频咔哒，检测器报它是对的。
    /// </summary>
    [Fact]
    public void ChordChangesWithoutDrums_ThroughTheAnalyzer_AreNotOnsets()
    {
        double[][] chords = [[220, 277, 330], [247, 311, 370], [196, 247, 294], [262, 330, 392]];
        var random = new Random(2);
        var onsets = CountThroughAnalyzer(5, t =>
        {
            var chord = chords[(int)(t / 0.5) % chords.Length];
            var within = t % 0.5;
            var envelope = Math.Min(1, Math.Min(within, 0.5 - within) / 0.03);
            var pad = chord.Sum(f => Math.Sin(2 * Math.PI * f * t)) * 0.1 * envelope;
            return pad + 0.01 * (random.NextDouble() * 2 - 1);
        });

        Assert.True(onsets <= 2, $"换音不应被当作鼓点，实际 {onsets} 次");
    }

    /// <summary>按 60fps 把合成的单声道信号（-1..1）喂进真实分析器，再逐帧喂给检测器。</summary>
    private static int CountThroughAnalyzer(double seconds, Func<double, double> signal)
    {
        const int rate = 48000;
        long nowMs = 0;
        var analyzer = new AudioSpectrumAnalyzer(() => nowMs);
        var detector = new OnsetDetector();
        var written = 0L;
        var onsets = 0;
        for (var tick = 0; tick < seconds * 60; tick++)
        {
            nowMs = tick * 1000L / 60;
            var frames = (int)(tick * rate / 60 - written);
            var pcm = new byte[frames * 4];
            for (var f = 0; f < frames; f++, written++)
            {
                var value = (short)Math.Clamp(signal(written / (double)rate) * short.MaxValue, short.MinValue, short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(f * 4), value);
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(f * 4 + 2), value);
            }

            if (frames > 0) analyzer.Submit(new AudioFrame(pcm, 0, rate, 2, IsSilent: false));
            if (detector.Update(analyzer.Capture().Spectrum, tick * Frame)) onsets++;
        }

        return onsets;
    }

    [Fact]
    public void FluctuationsBelowTheSilenceGate_AreNeverOnsets()
    {
        // 幅度总和约 64 × 1e-5 ≪ 门限：近零谱上做除法会把抖动放大成满幅通量。
        var onsets = CountOnsets(new OnsetDetector(), i => Flat(i % 2 == 0 ? 0f : 1e-5f), 120);

        Assert.Equal(0, onsets);
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
