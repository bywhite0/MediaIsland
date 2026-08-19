using System.Buffers.Binary;
using MediaIsland.Services.Audio.Visualization.Fft;

namespace MediaIsland.Tests.RealDevice;

/// <summary>单频信号的分析结果。</summary>
/// <param name="PeakHz">主峰频率。</param>
/// <param name="PeakMagnitude">主峰及其相邻两 bin 的幅度之和。</param>
/// <param name="NoiseMagnitude">其余 bin 的平均幅度。</param>
internal readonly record struct ToneResult(double PeakHz, double PeakMagnitude, double NoiseMagnitude)
{
    /// <summary>
    /// 信噪比。噪声为零时返回正无穷——那是合成信号的情形，不是缺陷。
    /// </summary>
    public double SignalToNoise =>
        NoiseMagnitude <= 0 ? double.PositiveInfinity : PeakMagnitude / NoiseMagnitude;
}

/// <summary>
/// 真机判据用的信号分析。
///
/// 谱峰是主判据：它一条覆盖「出声了」「不是静音也不是噪声」「重采样比算对了」三件事，
/// 且不受延迟影响——播放路径上有抖动缓冲、端点缓冲与重采样群延迟，
/// 逐样本比对要先对齐时间轴，而对齐需要一个正确的时钟，那是循环论证。
/// </summary>
internal static class ToneAnalysis
{
    /// <summary>FFT 长度。48000Hz 下 bin 宽度约 11.7Hz，对 1kHz 判据足够。</summary>
    public const int FftLength = 4096;

    /// <summary>忽略最低几个 bin：直流与极低频常有端点混音的残留。</summary>
    private const int LowBinCutoff = 3;

    /// <summary>
    /// 取一段样本做谱分析。
    ///
    /// 取中段而非开头：开头有预填充静音与起播过渡，把它算进谱里会压低信噪比，
    /// 而那与「根本没出声」混在一起。起播接缝由 <see cref="MaxAbsoluteStep"/> 单独看。
    /// </summary>
    public static ToneResult Analyze(ReadOnlySpan<byte> pcm, int sampleRate, int channels)
    {
        var samples = ExtractMonoMidSection(pcm, channels);
        if (samples is null)
        {
            return new ToneResult(0, 0, 0);
        }

        var real = samples;
        var imaginary = new float[FftLength];
        RealFft.ApplyHannWindow(real);
        RealFft.Transform(real, imaginary);

        var magnitudes = new float[FftLength / 2];
        RealFft.Magnitudes(real, imaginary, magnitudes);

        var peakBin = LowBinCutoff;
        for (var i = LowBinCutoff; i < magnitudes.Length; i++)
        {
            if (magnitudes[i] > magnitudes[peakBin])
            {
                peakBin = i;
            }
        }

        // 主峰算三个 bin：Hann 窗把单频的能量摊到峰值及其左右各一格。
        // 只取一个 bin 会让信噪比随频率是否恰好落在 bin 中心而起落。
        double peak = 0;
        double rest = 0;
        var restCount = 0;
        for (var i = LowBinCutoff; i < magnitudes.Length; i++)
        {
            if (Math.Abs(i - peakBin) <= 1)
            {
                peak += magnitudes[i];
            }
            else
            {
                rest += magnitudes[i];
                restCount++;
            }
        }

        return new ToneResult(
            PeakHz: (double)peakBin * sampleRate / FftLength,
            PeakMagnitude: peak,
            NoiseMagnitude: restCount == 0 ? 0 : rest / restCount);
    }

    /// <summary>
    /// 相邻样本的最大跳变（取第一声道）。
    ///
    /// 爆音的数值形式。对 A 乘 sin(2πft/fs)，相邻样本差的上界是
    /// <see cref="MaxStepBoundFor"/>；超出它意味着波形上有不连续。
    /// </summary>
    public static int MaxAbsoluteStep(ReadOnlySpan<byte> pcm, int channels)
    {
        if (channels <= 0)
        {
            return 0;
        }

        var stride = channels * sizeof(short);
        var frames = pcm.Length / stride;
        if (frames < 2)
        {
            return 0;
        }

        var max = 0;
        var previous = BinaryPrimitives.ReadInt16LittleEndian(pcm[..sizeof(short)]);
        for (var i = 1; i < frames; i++)
        {
            var current = BinaryPrimitives.ReadInt16LittleEndian(
                pcm.Slice(i * stride, sizeof(short)));
            var step = Math.Abs(current - previous);
            if (step > max)
            {
                max = step;
            }

            previous = current;
        }

        return max;
    }

    /// <summary>正弦相邻样本差的理论上界：导数的峰值乘以采样周期。</summary>
    public static double MaxStepBoundFor(double amplitude, double frequencyHz, int sampleRate) =>
        amplitude * 2 * Math.PI * frequencyHz / sampleRate;

    /// <summary>
    /// 抽中段，混成单声道 float。样本不足一个 FFT 窗时返回 null——
    /// 那是「采回的东西太少」，与「采回的东西不对」是不同的失败，调用方要能分开。
    /// </summary>
    private static float[]? ExtractMonoMidSection(ReadOnlySpan<byte> pcm, int channels)
    {
        if (channels <= 0)
        {
            return null;
        }

        var stride = channels * sizeof(short);
        var frames = pcm.Length / stride;
        if (frames < FftLength)
        {
            return null;
        }

        // 从四分之一处起：跳过起播段，又不至于取到收尾的停播过渡。
        var start = Math.Min(frames / 4, frames - FftLength);
        var samples = new float[FftLength];
        for (var i = 0; i < FftLength; i++)
        {
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(
                pcm.Slice((start + i) * stride, sizeof(short)));
        }

        return samples;
    }
}
