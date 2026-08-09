using System.Buffers.Binary;
using MediaIsland.Services.Audio;
using MediaIsland.Services.Audio.Visualization;
using Xunit;

namespace MediaIsland.Tests.Audio;

/// <summary>
/// 分析器有两条独立的契约要锁：频谱算得对不对，以及闸门放不放行。
///
/// 后者是本类的重点。FFT 的正确性已由 RealFft 的测试用解析信号锁死，这里只需确认
/// 「PCM 进去、峰值落在该落的地方」这条链路接对了；而闸门是本层特有的——它决定了
/// N 个频谱组件各自轮询时到底算几遍，算错方向的两种后果都很糟：放行太松会让
/// 每帧算好几次 FFT，太紧则会让频谱卡住不动。
///
/// 时钟一律注入。用真实时钟测「间隔小于 8ms」等于靠 Sleep 赌调度，在负载高的
/// CI 上必然偶发。
/// </summary>
public class AudioSpectrumAnalyzerTests
{
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const int Window = AudioSpectrumAnalyzer.WindowSize;

    /// <summary>i16 满量程的 0.977 倍。留一点余量，避免归一后恰好顶到 1.0 被 Clamp 削平。</summary>
    private const short Amplitude = 32000;

    private long _now;

    private AudioSpectrumAnalyzer NewAnalyzer() => new(() => _now);

    /// <summary>整帧同相的立体声正弦。左右同值，混单声道后幅度不变。</summary>
    private static AudioFrame Sine(float frequency, int frames, bool isSilent = false)
    {
        var pcm = new byte[frames * Channels * sizeof(short)];
        for (var f = 0; f < frames; f++)
        {
            var value = (short)(MathF.Sin(2 * MathF.PI * frequency * f / SampleRate) * Amplitude);
            for (var c = 0; c < Channels; c++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(
                    pcm.AsSpan((f * Channels + c) * sizeof(short)), value);
            }
        }

        return new AudioFrame(pcm, 0, SampleRate, Channels, isSilent);
    }

    private static AudioFrame Silence(int frames) =>
        new(new byte[frames * Channels * sizeof(short)], 0, SampleRate, Channels, true);

    private static int PeakBin(IReadOnlyList<float> spectrum)
    {
        var peak = 0;
        for (var i = 1; i < spectrum.Count; i++)
        {
            if (spectrum[i] > spectrum[peak])
            {
                peak = i;
            }
        }

        return peak;
    }

    /// <summary>期望 bin：频率除以 bin 宽度（sampleRate / WindowSize）。</summary>
    private static int ExpectedBin(float frequency) =>
        (int)MathF.Round(frequency * Window / SampleRate);

    // ---- 频谱正确性 ----

    [Fact]
    public void FullWindowOfSine_PeaksAtTheAnalyticBin()
    {
        var analyzer = NewAnalyzer();
        analyzer.Submit(Sine(1000f, Window));

        var snapshot = analyzer.Capture();

        // 1000Hz / (48000/2048) = 42.67，能量分摊在相邻两 bin，加 Hann 窗后主瓣约 2 bin 宽，
        // 故容差取 ±2 而非 ±1：谁略高取决于小数部分，不该由测试规定。
        Assert.Equal(43, ExpectedBin(1000f));
        Assert.InRange(PeakBin(snapshot.Spectrum), 41, 45);
    }

    [Fact]
    public void HigherFrequency_MovesThePeakAndLeavesTheLowBinsQuiet()
    {
        var analyzer = NewAnalyzer();
        analyzer.Submit(Sine(5000f, Window));

        var snapshot = analyzer.Capture();

        // 只验峰值位置无法排除频率轴整体偏移或镜像，故同时钉住「不在 1000Hz 那一带」。
        Assert.Equal(213, ExpectedBin(5000f));
        var peak = PeakBin(snapshot.Spectrum);
        Assert.InRange(peak, 211, 215);
        Assert.NotInRange(peak, 41, 45);
    }

    [Fact]
    public void SilentWindow_ProducesZeroSpectrumAndLevels()
    {
        var analyzer = NewAnalyzer();
        analyzer.Submit(Silence(Window));

        var snapshot = analyzer.Capture();

        // 先确认这是一份实算出来的快照。空快照的谱是空数组、电平是 0、IsSilent 为真——
        // 下面每一条断言它都恰好满足，不钉住长度的话，「分析器根本没算」会伪装成通过。
        Assert.Equal(AudioSpectrumAnalyzer.SpectrumBinCount, snapshot.Spectrum.Count);
        Assert.True(snapshot.Revision > 0);

        // 全零 PCM 每一步蝶形都只在 0 之间加减，不产生浮点误差，故要求精确 0。
        Assert.All(snapshot.Spectrum, magnitude => Assert.Equal(0f, magnitude));
        Assert.Equal(0f, snapshot.Rms);
        Assert.Equal(0f, snapshot.Peak);
        Assert.True(snapshot.IsSilent);
    }

    [Fact]
    public void SpectrumLength_IsConstantRegardlessOfInput()
    {
        var analyzer = NewAnalyzer();
        analyzer.Submit(Sine(1000f, Window));

        var snapshot = analyzer.Capture();

        // 定长原始谱，不随任何组件配置变化——频段映射是每个组件各自的显示口味，
        // 放在这个单例里会让一个组件改段数时另一个跟着变。
        Assert.Equal(1024, AudioSpectrumAnalyzer.SpectrumBinCount);
        Assert.Equal(AudioSpectrumAnalyzer.SpectrumBinCount, snapshot.Spectrum.Count);
    }

    [Fact]
    public void SpectrumValues_StayWithinUnitRange()
    {
        var analyzer = NewAnalyzer();
        analyzer.Submit(Sine(1000f, Window));

        var snapshot = analyzer.Capture();

        // 归一由这一层负责（RealFft 刻意不做），渲染层直接把值当高度比例用，越界会画到控件外。
        Assert.All(snapshot.Spectrum, magnitude => Assert.InRange(magnitude, 0f, 1f));
    }

    // ---- 不足一窗 ----

    [Fact]
    public void LessThanOneWindow_ReturnsEmptySnapshotWithoutThrowing()
    {
        var analyzer = NewAnalyzer();

        // 480 采样/声道是实测的单帧长度。一帧远不足一窗，这是启动后的常态而非异常。
        analyzer.Submit(Sine(1000f, 480));
        var snapshot = analyzer.Capture();

        // 返回空谱而非半窗垃圾：补零会在频谱上表现为一个真实存在的低频分量。
        Assert.Empty(snapshot.Spectrum);
        Assert.Equal(0L, snapshot.Revision);
    }

    [Fact]
    public void AccumulatingToAFullWindow_StartsProducingSpectrum()
    {
        var analyzer = NewAnalyzer();

        // 攒够之前一直空，攒够之后立刻出——中间不需要任何显式的「就绪」信号。
        for (var i = 0; i < 4; i++)
        {
            analyzer.Submit(Sine(1000f, 480));
            _now += 20;
            Assert.Empty(analyzer.Capture().Spectrum);
        }

        analyzer.Submit(Sine(1000f, 480));
        _now += 20;

        Assert.Equal(AudioSpectrumAnalyzer.SpectrumBinCount, analyzer.Capture().Spectrum.Count);
    }

    // ---- 陈旧度闸门 ----

    [Fact]
    public void FirstCaptureWithEnoughData_DoesNotWaitForTheGate()
    {
        // 闸门限的是「两次实算之间」的间隔，而第一次实算没有「上一次」可比。
        // 若用一个哨兵初值直接参与减法，long 溢出会把差值算成负数，
        // 于是它被当成「刚算过」——分析器在第一帧就卡死，永远返回空快照，
        // 频谱从头到尾不出来，且没有任何异常。
        var analyzer = NewAnalyzer();
        analyzer.Submit(Sine(1000f, Window));

        var snapshot = analyzer.Capture();

        Assert.Equal(AudioSpectrumAnalyzer.SpectrumBinCount, snapshot.Spectrum.Count);
        Assert.True(snapshot.Revision > 0, $"第一次 Capture 应产出快照，实际 Revision = {snapshot.Revision}");
    }

    [Fact]
    public void CaptureWithoutNewFrames_ReusesTheCachedSnapshot()
    {
        var analyzer = NewAnalyzer();
        analyzer.Submit(Sine(1000f, Window));

        var first = analyzer.Capture();
        _now += 100;
        var second = analyzer.Capture();

        // 先确认第一次真的算过——否则两次都是空快照，Revision 也「相等」，这条会假通过。
        Assert.True(first.Revision > 0);

        // 时钟推得再远也没用：没有新数据就没有可算的东西。
        Assert.Equal(first.Revision, second.Revision);
        Assert.Same(first, second);
    }

    [Fact]
    public void NewFramesWithinTheGateInterval_DoNotRecompute()
    {
        var analyzer = NewAnalyzer();
        analyzer.Submit(Sine(1000f, Window));
        var first = analyzer.Capture();

        _now += 4;
        analyzer.Submit(Sine(5000f, Window));
        var second = analyzer.Capture();

        Assert.True(first.Revision > 0);

        // 这道闸门就是「共享分析器」相对「每个组件自持分析器」的全部收益所在：
        // N 个组件各自轮询，实际只算一遍。
        Assert.Equal(first.Revision, second.Revision);
    }

    [Fact]
    public void NewFramesAfterTheGateInterval_Recompute()
    {
        var analyzer = NewAnalyzer();
        analyzer.Submit(Sine(1000f, Window));
        var first = analyzer.Capture();

        _now += 20;
        analyzer.Submit(Sine(5000f, Window));
        var second = analyzer.Capture();

        Assert.True(second.Revision > first.Revision);
        // 内容也真的换了，而不只是版本号涨了。
        Assert.InRange(PeakBin(second.Spectrum), 211, 215);
    }

    // ---- 静音标记与电平 ----

    [Fact]
    public void SilentFrameFollowedByAudible_FlipsIsSilent()
    {
        var analyzer = NewAnalyzer();
        analyzer.Submit(Silence(Window));
        var silent = analyzer.Capture();

        _now += 20;
        analyzer.Submit(Sine(1000f, Window));
        var audible = analyzer.Capture();

        // 静音标记来自帧头而非能量判定：上游明确告知「这段是静音」比本地猜阈值可靠。
        Assert.True(silent.IsSilent);
        Assert.False(audible.IsSilent);
    }

    [Theory]
    [InlineData(1000f)]
    [InlineData(5000f)]
    public void PeakIsNeverBelowRms(float frequency)
    {
        var analyzer = NewAnalyzer();
        analyzer.Submit(Sine(frequency, Window));

        var snapshot = analyzer.Capture();

        // 均方根不可能超过最大绝对值。若哪天给 Peak 加了上限裁剪而 Rms 没有，
        // 越界输入下这条会红——电平表的峰值刻线会跑到填充条左边。
        Assert.True(snapshot.Peak >= snapshot.Rms, $"peak = {snapshot.Peak}, rms = {snapshot.Rms}");
        Assert.InRange(snapshot.Rms, 0f, 1f);
        Assert.InRange(snapshot.Peak, 0f, 1f);
    }

    // ---- 波形抽样 ----

    [Fact]
    public void Waveform_IsSubsampledAndWithinUnitRange()
    {
        var analyzer = NewAnalyzer();
        analyzer.Submit(Sine(1000f, Window));

        var snapshot = analyzer.Capture();

        // 抽样点数是实现细节，但必须稳定且远小于窗长：组件按它分配包络数组，
        // 长度漂移会让数组每帧重建；等于窗长则每帧要往 UI 递 2048 个 float。
        Assert.Equal(128, snapshot.Waveform.Count);
        Assert.True(snapshot.Waveform.Count < Window);
        Assert.All(snapshot.Waveform, sample => Assert.InRange(sample, -1f, 1f));
    }

    [Fact]
    public void Waveform_IsSampledBeforeWindowing()
    {
        var analyzer = NewAnalyzer();
        analyzer.Submit(Sine(1000f, Window));

        var snapshot = analyzer.Capture();

        // Hann 窗把首尾压到 0。若在加窗之后抽样，示波器会显示两头收窄的假象——
        // 那是分析器的加工痕迹，不是信号本身。取首尾附近的点验证它们没有被压扁：
        // 满幅正弦在任意 16 个采样的跨度里不可能全部接近 0。
        var head = snapshot.Waveform.Take(8).Select(MathF.Abs).Max();
        var tail = snapshot.Waveform.TakeLast(8).Select(MathF.Abs).Max();

        Assert.True(head > 0.1f, $"波形头部被压扁：{head}");
        Assert.True(tail > 0.1f, $"波形尾部被压扁：{tail}");
    }

    // ---- 帧入口 ----

    [Fact]
    public async Task OnFrameAsync_FeedsTheSameBufferAsSubmit()
    {
        var analyzer = NewAnalyzer();

        await analyzer.OnFrameAsync(Sine(1000f, Window), CancellationToken.None);

        // 音频线程入口只是同步入口的包装，不该存在第二条数据路径。
        Assert.InRange(PeakBin(analyzer.Capture().Spectrum), 41, 45);
    }

    [Fact]
    public void MalformedFrames_AreIgnoredInsteadOfThrowing()
    {
        var analyzer = NewAnalyzer();

        // 声道数为 0 会让环形缓冲抛参数异常。这条路径跑在音频采集回调上，
        // 异常穿过去会打断采集本身——挡在入口比让它传下去便宜得多。
        analyzer.Submit(new AudioFrame([], 0, SampleRate, Channels, false));
        analyzer.Submit(new AudioFrame(new byte[8], 0, SampleRate, 0, false));

        Assert.Empty(analyzer.Capture().Spectrum);
    }

    [Fact]
    public void SampleRate_ReflectsTheLatestFrame()
    {
        var analyzer = NewAnalyzer();

        // 默认值要能用：组件可能在第一帧到达前就开始换算频率轴。
        Assert.Equal(48000, analyzer.SampleRate);

        analyzer.Submit(new AudioFrame(new byte[Channels * sizeof(short)], 0, 44100, Channels, false));

        // 组件用它把 bin 下标换算成频率，取错会让整条频率轴偏。
        Assert.Equal(44100, analyzer.SampleRate);
    }

    [Fact]
    public void ZeroSampleRateFrames_DoNotOverwriteTheKnownRate()
    {
        var analyzer = NewAnalyzer();
        analyzer.Submit(Sine(1000f, 480));

        analyzer.Submit(new AudioFrame(new byte[Channels * sizeof(short)], 0, 0, Channels, false));

        // 0 不是一个采样率，是「不知道」。用它覆盖已知值会让频率换算除以零。
        Assert.Equal(SampleRate, analyzer.SampleRate);
    }
}
