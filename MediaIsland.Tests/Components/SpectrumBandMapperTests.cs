using MediaIsland.Controls;
using Xunit;

namespace MediaIsland.Tests.Components;

/// <summary>
/// 定长幅度谱 → 显示用频段。这一层在组件侧而非分析层，因为段数与频率范围是
/// 每个组件各自的显示口味，而 FFT 是所有组件共享的昂贵计算——分析器是单例，
/// 把映射放进去会让一个组件改段数时另一个跟着变。
///
/// 谱长固定 1024（FFT 2048 点的一半），故 bin 宽度 = 48000 / 2048 = 23.4375Hz。
/// 测试直接在指定 bin 写 1.0 模拟峰值，比合成正弦再做 FFT 更能精确定位「哪个 bin」。
/// </summary>
public class SpectrumBandMapperTests
{
    private const int SampleRate = 48000;
    private const int BinCount = 1024;
    private const double BinHz = (double)SampleRate / (BinCount * 2);

    private const double MinHz = 80;
    private const double MaxHz = 2000;

    private static float[] SpectrumWithPeakAt(params int[] bins)
    {
        var spectrum = new float[BinCount];
        foreach (var bin in bins)
        {
            spectrum[bin] = 1f;
        }

        return spectrum;
    }

    private static int PeakBand(float[] bands)
    {
        var peak = 0;
        for (var i = 1; i < bands.Length; i++)
        {
            if (bands[i] > bands[peak])
            {
                peak = i;
            }
        }

        return peak;
    }

    // ---- 频段定位 ----

    [Fact]
    public void MidFrequencyPeak_LandsInTheTopBand()
    {
        // bin 43 ≈ 1008Hz。80–2000 分 4 段是对数分布，最后一段起点约 894Hz，
        // 故 1008Hz 落在最后一段——线性分段会把它算到第 2 段，这条能区分两者。
        var bands = SpectrumBandMapper.Map(SpectrumWithPeakAt(43), SampleRate, 4, MinHz, MaxHz);

        Assert.Equal(4, bands.Length);
        Assert.Equal(3, PeakBand(bands));
    }

    [Fact]
    public void LowFrequencyPeak_LandsInTheBottomBand()
    {
        // bin 5 ≈ 117Hz，落在第一段（80–179）。
        var bands = SpectrumBandMapper.Map(SpectrumWithPeakAt(5), SampleRate, 4, MinHz, MaxHz);

        Assert.Equal(0, PeakBand(bands));
    }

    [Fact]
    public void BandsTakeTheMaximumWithinTheirRangeNotTheAverage()
    {
        // 同一段内一高一低，结果必须等于高的那个。取平均会被段内的空白 bin 稀释——
        // 高频段动辄跨几十个 bin，平均下来视觉上整片发闷。
        var spectrum = new float[BinCount];
        spectrum[43] = 0.9f;
        spectrum[44] = 0.1f;

        var bands = SpectrumBandMapper.Map(spectrum, SampleRate, 4, MinHz, MaxHz);

        Assert.Equal(0.9f, bands[3]);
    }

    // ---- 边界 ----

    [Fact]
    public void SilentSpectrum_YieldsZerosOfTheRequestedLength()
    {
        var bands = SpectrumBandMapper.Map(new float[BinCount], SampleRate, 8, MinHz, MaxHz);

        Assert.Equal(8, bands.Length);
        Assert.All(bands, value => Assert.Equal(0f, value));
    }

    [Fact]
    public void EmptySpectrum_YieldsZerosInsteadOfThrowing()
    {
        // 分析器在攒够一窗之前返回空谱，这是启动后的常态而非异常。
        var bands = SpectrumBandMapper.Map([], SampleRate, 16, MinHz, MaxHz);

        Assert.Equal(16, bands.Length);
        Assert.All(bands, value => Assert.Equal(0f, value));
    }

    [Fact]
    public void SingleBand_ReducesToTheMaximumOverTheWholeRange()
    {
        var bands = SpectrumBandMapper.Map(SpectrumWithPeakAt(43), SampleRate, 1, MinHz, MaxHz);

        Assert.Single(bands);
        Assert.Equal(1f, bands[0]);
    }

    [Fact]
    public void MoreBandsThanBins_DoesNotThrow()
    {
        // 段比 bin 密时相邻段会重复取同一个 bin。视觉上是台阶，但不该是异常——
        // 用户可以把段数调到 64 而把频率范围收得很窄。
        var bands = SpectrumBandMapper.Map(SpectrumWithPeakAt(43), SampleRate, 64, MinHz, 200);

        Assert.Equal(64, bands.Length);
    }

    [Fact]
    public void RangeEntirelyAboveTheSpectrum_YieldsZerosWithoutOverrunning()
    {
        // 48kHz 采样的谱最高只到 24kHz。用户把范围调到 30k–40k 是允许的，
        // 结果该是全零而不是越界。
        var bands = SpectrumBandMapper.Map(SpectrumWithPeakAt(43), SampleRate, 4, 30000, 40000);

        Assert.Equal(4, bands.Length);
        Assert.All(bands, value => Assert.Equal(0f, value));
    }

    [Theory]
    [InlineData(2000d, 80d)]      // 上下颠倒
    [InlineData(500d, 500d)]      // 上下相等，段宽为 0
    [InlineData(0d, 2000d)]       // 下限为 0，取对数会得到负无穷
    [InlineData(-100d, 2000d)]
    public void InvalidRange_Throws(double min, double max)
    {
        Assert.Throws<ArgumentException>(() =>
            SpectrumBandMapper.Map(SpectrumWithPeakAt(43), SampleRate, 4, min, max));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveBandCount_Throws(int bandCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SpectrumBandMapper.Map(SpectrumWithPeakAt(43), SampleRate, bandCount, MinHz, MaxHz));
    }

    // ---- 能量归约 ----

    [Fact]
    public void Energy_InsideTheRange_IsSignificant()
    {
        Assert.Equal(1f, SpectrumBandMapper.Energy(SpectrumWithPeakAt(43), SampleRate, MinHz, MaxHz));
    }

    [Fact]
    public void Energy_OutsideTheRange_IsZero()
    {
        // bin 213 ≈ 5000Hz，在 80–2000 之外。律动条只跟随限定频段，
        // 否则高频的嘶声会让它一直亮着。
        Assert.Equal(0f, SpectrumBandMapper.Energy(SpectrumWithPeakAt(213), SampleRate, MinHz, MaxHz));
    }

    [Fact]
    public void Energy_OnEmptySpectrum_IsZero()
    {
        Assert.Equal(0f, SpectrumBandMapper.Energy([], SampleRate, MinHz, MaxHz));
    }

    [Fact]
    public void Energy_RejectsInvalidRangeLikeMapDoes()
    {
        // 两个入口的校验必须同源，否则调用方无法从一个方法的行为推断另一个。
        Assert.Throws<ArgumentException>(() =>
            SpectrumBandMapper.Energy(SpectrumWithPeakAt(43), SampleRate, 2000, 80));
    }

    [Fact]
    public void BinWidthDerivesFromSpectrumLengthNotAHardcodedWindow()
    {
        // 谱长是 FFT 长度的一半，故 bin 宽度 = sampleRate / (count * 2)。
        // 写死 2048 的话，日后改帧长会让整条频率轴静默偏移。
        // 用半长的谱验证：同一个 bin 下标此时对应两倍频率。
        var halfSpectrum = new float[BinCount / 2];
        halfSpectrum[43] = 1f;

        // bin 43 在 512 长的谱里 ≈ 2015Hz，已越过 2000 的上限。
        Assert.Equal(0f, SpectrumBandMapper.Energy(halfSpectrum, SampleRate, MinHz, MaxHz));
        // 放宽上限即可重新落入。
        Assert.Equal(1f, SpectrumBandMapper.Energy(halfSpectrum, SampleRate, MinHz, 3000));
        Assert.True(BinHz > 0);
    }
}
