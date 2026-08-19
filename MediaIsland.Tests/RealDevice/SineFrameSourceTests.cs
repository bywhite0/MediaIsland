using System.Buffers.Binary;
using Xunit;

namespace MediaIsland.Tests.RealDevice;

/// <summary>
/// 合成源与判定函数自身的判据。
///
/// 为什么这些不是多余的：真机测试的结论全部经由这两个工具得出。若谱峰分析找错了频率，
/// 或差分上界算错了，真机套件会给出一个看起来精确的错误结论——而那比没有判据更坏。
/// 这些测试不需要真机，故不带门控。
/// </summary>
public class SineFrameSourceTests
{
    private const int SampleRate = 48_000;
    private const double Amplitude = 0.5 * short.MaxValue;

    /// <summary>合成一段单频立体声 i16 PCM，零相位起始。</summary>
    private static byte[] Synthesize(double frequencyHz, int frames, double amplitude = Amplitude)
    {
        var pcm = new byte[frames * 2 * sizeof(short)];
        for (var i = 0; i < frames; i++)
        {
            var value = (short)Math.Round(
                amplitude * Math.Sin(2 * Math.PI * frequencyHz * i / SampleRate));
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4, 2), value);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4 + 2, 2), value);
        }

        return pcm;
    }

    [Fact]
    public void Analyze_FindsTheInjectedFrequency()
    {
        var pcm = Synthesize(1_000, ToneAnalysis.FftLength * 2);

        var tone = ToneAnalysis.Analyze(pcm, SampleRate, channels: 2);

        // bin 宽度是 48000/4096 约 11.7Hz。容差取两个 bin：加窗后主峰跨约 3 个 bin，
        // 峰值 bin 可能落在真实频率的相邻一格。
        var binWidth = (double)SampleRate / ToneAnalysis.FftLength;
        Assert.InRange(tone.PeakHz, 1_000 - 2 * binWidth, 1_000 + 2 * binWidth);
    }

    [Fact]
    public void Analyze_DistinguishesAToneFromSilence()
    {
        // 静音的判据不能只看「峰值频率对不对」——全零信号的峰值 bin 是任意的。
        // 信噪比是分开这两者的那个量。
        var tone = ToneAnalysis.Analyze(Synthesize(1_000, ToneAnalysis.FftLength * 2), SampleRate, 2);
        var silence = ToneAnalysis.Analyze(new byte[ToneAnalysis.FftLength * 2 * 4], SampleRate, 2);

        Assert.True(tone.SignalToNoise > 10, $"纯正弦的信噪比只有 {tone.SignalToNoise:F1}");
        Assert.True(silence.PeakMagnitude < 1e-3, $"静音却有峰值 {silence.PeakMagnitude}");
    }

    [Fact]
    public void Analyze_RejectsBroadbandNoise()
    {
        // 白噪声的能量摊在所有 bin 上，信噪比必然低。这一条防的是
        // 「端点在响，但响的不是我们注入的东西」被判成通过。
        var random = new Random(20260819);
        var pcm = new byte[ToneAnalysis.FftLength * 2 * 4];
        for (var i = 0; i < pcm.Length / 2; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(
                pcm.AsSpan(i * 2, 2), (short)random.Next(short.MinValue / 2, short.MaxValue / 2));
        }

        var noise = ToneAnalysis.Analyze(pcm, SampleRate, 2);

        Assert.True(noise.SignalToNoise < 10, $"白噪声的信噪比高达 {noise.SignalToNoise:F1}");
    }

    [Fact]
    public void Analyze_ReportsNothingWhenThereIsNotEvenOneWindow()
    {
        // 「采回的东西太少」与「采回的东西不对」是不同的失败，调用方要能分开。
        var tone = ToneAnalysis.Analyze(new byte[16], SampleRate, 2);

        Assert.Equal(0, tone.PeakHz);
        Assert.Equal(0, tone.PeakMagnitude);
    }

    [Fact]
    public void MaxAbsoluteStep_StaysUnderTheTheoreticalBoundForASine()
    {
        var pcm = Synthesize(1_000, SampleRate / 10);
        var bound = ToneAnalysis.MaxStepBoundFor(Amplitude, 1_000, SampleRate);

        var step = ToneAnalysis.MaxAbsoluteStep(pcm, channels: 2);

        // 上界是 A 乘 2πf/fs。取整与端点效应会让实测略高，故留一格余量。
        Assert.True(step <= bound + 2, $"实测跳变 {step} 超出理论上界 {bound:F1}");
    }

    [Fact]
    public void MaxAbsoluteStep_CatchesAnInjectedDiscontinuity()
    {
        // 起播判据的正向对照：把一段正弦中间接上满量程反相的两帧，接缝处就是爆音。
        // 没有这一条，MaxAbsoluteStep 恒返回 0 也能让上一条通过。
        var pcm = Synthesize(1_000, SampleRate / 10);
        var mid = pcm.Length / 2 / 4 * 4;
        BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(mid, 2), short.MaxValue);
        BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(mid + 2, 2), short.MaxValue);
        BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(mid + 4, 2), short.MinValue);
        BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(mid + 6, 2), short.MinValue);

        var step = ToneAnalysis.MaxAbsoluteStep(pcm, channels: 2);
        var bound = ToneAnalysis.MaxStepBoundFor(Amplitude, 1_000, SampleRate);

        Assert.True(step > bound * 3, $"注入的不连续没被抓到：跳变 {step}，上界 {bound:F1}");
    }

    [Fact]
    public async Task SineFrameSource_EmitsTheDeclaredFormatWithContinuousPhase()
    {
        // 相位跨帧连续是起播判据成立的前提：若每帧都从零相位重新起算，
        // 帧接缝处会有一个合法的跳变，而那会让真机的爆音判据永远红。
        using var source = new SineFrameSource(frequencyHz: 1_000, amplitude: 0.5);
        var accumulated = new List<byte>();
        var frames = 0;
        source.FrameAvailable += frame =>
        {
            Assert.Equal(48_000, frame.SampleRate);
            Assert.Equal(2, frame.Channels);
            Assert.False(frame.IsSilent);
            lock (accumulated)
            {
                accumulated.AddRange(frame.Pcm);
                frames++;
            }
        };

        await source.StartAsync(CancellationToken.None);
        await Task.Delay(300);
        await source.StopAsync(CancellationToken.None);

        byte[] pcm;
        lock (accumulated)
        {
            Assert.True(frames >= 5, $"300ms 内只发了 {frames} 块");
            pcm = accumulated.ToArray();
        }

        var bound = ToneAnalysis.MaxStepBoundFor(0.5 * short.MaxValue, 1_000, 48_000);
        var step = ToneAnalysis.MaxAbsoluteStep(pcm, channels: 2);

        Assert.True(step <= bound + 2, $"帧接缝处有跳变 {step}，上界 {bound:F1}：相位没跨帧连续");
    }

    [Fact]
    public async Task SineFrameSource_KeepsUpWithTheWallClock()
    {
        // 注入速率必须逼近 48000Hz，否则缓冲占用的漂移判据会把「注入太慢」
        // 读成控制律失灵。Windows 定时器分辨率约 15.6ms，固定每轮一块会偏两成以上，
        // 故合成源按墙钟追赶——这一条正是钉住那个追赶。
        using var source = new SineFrameSource(frequencyHz: 1_000, amplitude: 0.5);
        await source.StartAsync(CancellationToken.None);
        await Task.Delay(1_000);
        await source.StopAsync(CancellationToken.None);

        // 一秒应产出约 50 块（20ms 一块）。容差正负两成，覆盖调度抖动。
        Assert.InRange(source.TotalFramesEmitted, 40, 60);
    }
}
