using MediaIsland.Services.Audio.Visualization.Fft;
using Xunit;

namespace MediaIsland.Tests.Audio;

/// <summary>
/// 用解析上已知的信号锁定频谱，而不是比对蝶形运算的中间值。
///
/// 这层对外只承诺一件事：某个频率的能量落在哪个 bin。可视化只读幅度谱，
/// 读不到位反转下标顺序、也读不到旋转因子，所以把断言压在中间值上等于把实现细节焊死，
/// 日后换成分裂基或换库时测试会整片红掉，却并不说明可视化坏了。
///
/// 期望 bin 一律由 freq / (sampleRate / size) 推出，容差 ±1 —— 正弦频率不是 bin 宽度的
/// 整数倍时，能量本就分摊在相邻两个 bin 上，谁略高取决于小数部分，不该由测试规定。
/// </summary>
public class RealFftTests
{
    private const int SampleRate = 48000;
    private const int Size = 2048;

    /// <summary>Size = 2048 下的 bin 宽度 48000 / 2048 = 23.4375Hz。</summary>
    private const float BinWidth = (float)SampleRate / Size;

    /// <summary>远离所有测试信号的 bin（Size = 2048 专用），作本底参照，用来区分「真峰」与「整片抬升」。</summary>
    private const int QuietBin = 700;

    /// <summary>
    /// 落在整 bin 上的频率：1500 = N/32 × (48000/N)，对本文件用到的每个 N 都是整数 bin。
    /// 整周期信号无泄漏，峰值幅度才能拿 N/2 这个解析值直接断言绝对标度。
    /// </summary>
    private const float ExactBinFrequency = 1500f;

    private static float[] Sine(float frequency, int size = Size)
    {
        var samples = new float[size];
        for (var i = 0; i < size; i++)
        {
            samples[i] = MathF.Sin(2 * MathF.PI * frequency * i / SampleRate);
        }

        return samples;
    }

    /// <summary>按生产代码的实际调用顺序取谱：变换后立刻算幅度，两者的契约要一起验。</summary>
    private static float[] Spectrum(float[] samples)
    {
        var real = (float[])samples.Clone();
        var imaginary = new float[samples.Length];
        RealFft.Transform(real, imaginary);

        var magnitudes = new float[samples.Length / 2];
        RealFft.Magnitudes(real, imaginary, magnitudes);
        return magnitudes;
    }

    private static int ExpectedBin(float frequency) => (int)MathF.Round(frequency / BinWidth);

    /// <summary>按实际 FFT 长度换算期望 bin：bin 宽度是 sampleRate / size，随 size 变。</summary>
    private static int ExpectedBin(float frequency, int size) =>
        (int)MathF.Round(frequency * size / SampleRate);

    private static int PeakBin(float[] magnitudes) => Array.IndexOf(magnitudes, magnitudes.Max());

    /// <summary>叠加信号没有全局唯一峰值，只能在期望 bin 的邻域里找局部峰。</summary>
    private static int PeakBinNear(float[] magnitudes, int center, int radius = 3)
    {
        // 低频信号的 center 可能小于 radius，此时窗口左边界必须夹到 0：
        // LINQ 的 Skip 对负数按 0 处理，但若 from 仍是负数，返回的下标会整体偏移。
        var from = Math.Max(0, center - radius);
        var window = magnitudes.Skip(from).Take(center + radius - from + 1).ToArray();
        return from + Array.IndexOf(window, window.Max());
    }

    // ---- 频率定位 ----

    [Fact]
    public void DcSignal_ConcentratesEnergyInBinZero()
    {
        // 全 1.0 没有任何振荡，能量必须全落 bin 0。这一条同时排除一类实现错误：
        // 若位反转或旋转因子写错，直流会散到高频 bin 上，其他信号的测试却可能仍偶然通过。
        var samples = new float[Size];
        Array.Fill(samples, 1.0f);

        var magnitudes = Spectrum(samples);

        Assert.Equal(0, PeakBin(magnitudes));
        var rest = magnitudes.Skip(1).Max();
        Assert.True(magnitudes[0] > rest * 100, $"bin 0 = {magnitudes[0]}, 其余最大 = {rest}");

        // 绝对标度必须钉住，不能只验比值：下游按「未归一化、bin 0 == N」这个约定自己做标定，
        // 若哪天在这里加了 1/N，整谱等比缩小，所有相对断言照样通过，而可视化会整片变矮。
        Assert.Equal(2048f, magnitudes[0], 1e-2);
    }

    [Fact]
    public void Sine1000Hz_PeaksAtBin43()
    {
        var magnitudes = Spectrum(Sine(1000f));

        var expected = ExpectedBin(1000f);
        Assert.Equal(43, expected);
        Assert.InRange(PeakBin(magnitudes), expected - 1, expected + 1);
    }

    [Fact]
    public void Sine5000Hz_PeaksAtBin213_NotAtLowFrequency()
    {
        // 同时钉住「不在 43」：只验峰值位置，无法排除幅度谱下标整体偏移或镜像的实现错误。
        var magnitudes = Spectrum(Sine(5000f));

        var expected = ExpectedBin(5000f);
        Assert.Equal(213, expected);
        var peak = PeakBin(magnitudes);
        Assert.InRange(peak, expected - 1, expected + 1);
        Assert.NotInRange(peak, 42, 44);
    }

    [Theory]
    [InlineData(512)]
    [InlineData(2048)]
    [InlineData(16384)]
    public void ExactBinSine_PeaksAtAnalyticBinAndAmplitude(int size)
    {
        // 覆盖最大端。旋转因子是逐点复数乘法递推出来的，误差随级数（log2 N）累积，
        // 帧长若从 2048 提到 16384 就多 3 级。把 N 参数化后，这条误差预算由测试守着，
        // 而不是只写在某份报告里——改帧长时会直接红。
        //
        // 1500Hz 对这三个 N 都落在整 bin 上（bin == N/32），整周期无泄漏，
        // 峰值幅度才有解析值 N/2（振幅 1 的实正弦，能量均分给正负频率）。
        var magnitudes = Spectrum(Sine(ExactBinFrequency, size));
        var peak = PeakBin(magnitudes);

        Assert.Equal(size / 32, ExpectedBin(ExactBinFrequency, size));
        Assert.Equal(size / 32, peak);
        // 容差取 N/2 的千分之一，即相对误差 1e-3；实测递推误差比这小一到两个数量级。
        Assert.Equal(size / 2f, magnitudes[peak], size * 5e-4);
    }

    [Fact]
    public void TwoSines_ProduceLocalPeaksAtBothFrequencies()
    {
        // 可视化的实际输入是混合信号。线性性若被破坏（例如某级蝶形漏乘旋转因子），
        // 单频测试仍可能通过，但两个分量会互相污染。
        var low = Sine(500f);
        var high = Sine(4000f);
        var mixed = new float[Size];
        for (var i = 0; i < Size; i++)
        {
            mixed[i] = low[i] + high[i];
        }

        var magnitudes = Spectrum(mixed);

        var lowBin = ExpectedBin(500f);
        var highBin = ExpectedBin(4000f);
        Assert.InRange(PeakBinNear(magnitudes, lowBin), lowBin - 1, lowBin + 1);
        Assert.InRange(PeakBinNear(magnitudes, highBin), highBin - 1, highBin + 1);
        Assert.True(magnitudes[lowBin] > magnitudes[QuietBin] * 10);
        Assert.True(magnitudes[highBin] > magnitudes[QuietBin] * 10);
    }

    [Fact]
    public void SilentInput_ProducesAllZeroMagnitudes()
    {
        // 静音帧是常态（无播放时）。这里必须是精确 0 而非近似 0：
        // 全零输入的每一步蝶形都只在 0 之间加减，不产生浮点误差，
        // 冒出非零值意味着读到了未初始化的缓冲。
        var magnitudes = Spectrum(new float[Size]);

        Assert.All(magnitudes, m => Assert.Equal(0f, m));
    }

    // ---- 参数校验 ----

    [Fact]
    public void NonPowerOfTwoSize_Throws()
    {
        // radix-2 分解要求长度是 2 的幂。宁可抛，也不要静默截断到 512 ——
        // 那会让频率轴悄悄错位，而调用方看不出任何异常。
        var real = new float[1000];
        var imaginary = new float[1000];

        Assert.Throws<ArgumentException>(() => RealFft.Transform(real, imaginary));
    }

    [Fact]
    public void SizeTwo_IsAccepted()
    {
        // 2 是 radix-2 的最小合法长度，边界不能被 n < 2 的检查连带排除。
        var real = new float[] { 1f, -1f };
        var imaginary = new float[2];

        RealFft.Transform(real, imaginary);

        Assert.Equal(0f, real[0]);
        Assert.Equal(2f, real[1]);
    }

    [Fact]
    public void SizeOne_Throws()
    {
        var real = new float[1];
        var imaginary = new float[1];

        Assert.Throws<ArgumentException>(() => RealFft.Transform(real, imaginary));
    }

    [Fact]
    public void MismatchedImaginaryLength_Throws()
    {
        var real = new float[Size];
        var imaginary = new float[Size / 2];

        Assert.Throws<ArgumentException>(() => RealFft.Transform(real, imaginary));
    }

    [Fact]
    public void MagnitudesWithMismatchedImaginaryLength_Throws()
    {
        var real = new float[Size];
        var imaginary = new float[Size / 2];
        var magnitudes = new float[Size / 2];

        Assert.Throws<ArgumentException>(() => RealFft.Magnitudes(real, imaginary, magnitudes));
    }

    [Fact]
    public void MagnitudesWithEmptySpans_Throws()
    {
        // 空 span 会让 0 == 0 / 2 这条长度校验通过、循环零次执行，于是静默返回。
        // 危害不在于空谱本身，而在于同一对长度 Transform 拒绝、Magnitudes 接受——
        // 调用方就无法从任一方法的行为推断另一个的契约。长度 1 同理。
        Assert.Throws<ArgumentException>(() =>
            RealFft.Magnitudes(Array.Empty<float>(), Array.Empty<float>(), Array.Empty<float>()));
        Assert.Throws<ArgumentException>(() =>
            RealFft.Magnitudes(new float[1], new float[1], Array.Empty<float>()));
    }

    [Fact]
    public void MagnitudesWithNonPowerOfTwoInput_Throws()
    {
        // 与 Transform 同源的校验：1000 点的谱不可能由 Transform 产出，就不该被 Magnitudes 受理。
        var real = new float[1000];
        var imaginary = new float[1000];

        Assert.Throws<ArgumentException>(() => RealFft.Magnitudes(real, imaginary, new float[500]));
    }

    [Fact]
    public void MagnitudesWithWrongOutputLength_Throws()
    {
        // 实信号谱共轭对称，后半是前半的镜像。长度写成 Size 会让可视化把镜像当成真频段画出来。
        var real = new float[Size];
        var imaginary = new float[Size];

        Assert.Throws<ArgumentException>(() => RealFft.Magnitudes(real, imaginary, new float[Size]));
        Assert.Throws<ArgumentException>(() => RealFft.Magnitudes(real, imaginary, new float[Size / 4]));
    }

    // ---- Hann 窗 ----

    [Fact]
    public void HannWindow_TapersEndsAndPreservesCenter()
    {
        var samples = new float[Size];
        Array.Fill(samples, 1.0f);

        RealFft.ApplyHannWindow(samples);

        Assert.Equal(0f, samples[0], 1e-6);
        Assert.Equal(0f, samples[^1], 1e-6);
        // 对称窗（除以 n - 1）在偶数长度下峰值落在中间两点之间，二者相等且已很接近 1。
        Assert.Equal(samples[Size / 2 - 1], samples[Size / 2], 1e-6);
        Assert.True(samples[Size / 2] > 0.999f, $"中点 = {samples[Size / 2]}");
    }

    [Fact]
    public void HannWindow_OnTooShortBuffer_IsNoOp()
    {
        // n - 1 会在长度 1 时变成除以 0。短缓冲无窗可加，原样返回比抛异常更合适：
        // 调用方按帧长切块，末尾出现 1 个样本的残块属正常情况。
        var single = new[] { 0.5f };
        var empty = Array.Empty<float>();

        RealFft.ApplyHannWindow(single);
        RealFft.ApplyHannWindow(empty);

        Assert.Equal(0.5f, single[0]);
    }

    [Fact]
    public void HannWindow_ReducesSpectralLeakageOfNonIntegerPeriodSignal()
    {
        // 加窗的唯一目的就是压泄漏，故直接对结果取证：取一个非整周期频率
        // （1000Hz 不是 23.4375Hz 的整数倍），比较远端本底。
        var raw = Spectrum(Sine(1000f));

        var windowed = Sine(1000f);
        RealFft.ApplyHannWindow(windowed);
        var tapered = Spectrum(windowed);

        Assert.True(
            tapered[QuietBin] < raw[QuietBin],
            $"加窗后远端本底 = {tapered[QuietBin]}, 未加窗 = {raw[QuietBin]}");
        Assert.InRange(PeakBin(tapered), ExpectedBin(1000f) - 1, ExpectedBin(1000f) + 1);
    }

    // ---- 测试辅助自身 ----

    [Fact]
    public void PeakBinNear_WithCenterBelowRadius_SearchesTheClampedWindow()
    {
        // 辅助函数出错不以异常暴露，只会让上面那些 InRange 落在错误的 bin 上——
        // 红或绿都不再可信，比产品代码的缺陷更难查。故对唯一含分支的辅助函数直接取证，
        // 而不是指望某条频率测试恰好路过这个分支：当前两个调用点的 center 是 21 与 171，
        // 都远大于 radius，窗口越界这条路径在整个文件里没有任何测试会走到。
        //
        // 构造同时钉住两侧：下标 1 是期望峰值，下标 5 更高但落在 center + radius = 4 之外。
        // 左边界不夹紧（from = -2）时，from + IndexOf 会把结果整体左移；
        // 窗口长度写成 2 * radius + 1 而不减去夹紧量时，右边界外移会把 5 号误当成峰值。
        // 两种写法都返回不等于 1 的值。
        var magnitudes = new float[16];
        magnitudes[1] = 1f;
        magnitudes[5] = 2f;

        Assert.Equal(1, PeakBinNear(magnitudes, center: 1));
    }
}
