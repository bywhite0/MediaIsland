using System.Buffers.Binary;
using MediaIsland.Services.Audio;
using MediaIsland.Services.Audio.Visualization;
using Xunit;

namespace MediaIsland.Tests.Audio;

/// <summary>
/// 门面本身几乎没有逻辑，值得锁的是它的**边界**：两个帧源都提交到这里，组件也只认这里，
/// 而它自己不做任何源仲裁。
///
/// 「不做仲裁」是一条需要被测试保护的负向契约——它看起来像是缺功能，
/// 很容易被后来者「补上」。但仲裁需要知道「上游连没连」，那是 MediaLink 层的连接态问题，
/// 而本层按分层约束不知晓协议存在。真在这里加过滤，就等于把协议概念漏进了音频层。
/// </summary>
public class AudioVisualizationServiceTests
{
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const int Window = AudioSpectrumAnalyzer.WindowSize;

    private long _now;

    private (AudioVisualizationService Service, AudioSpectrumAnalyzer Analyzer) NewService()
    {
        var analyzer = new AudioSpectrumAnalyzer(() => _now);
        return (new AudioVisualizationService(new AudioVisualizationDemand(), analyzer), analyzer);
    }

    private static AudioFrame Sine(float frequency, int frames)
    {
        var pcm = new byte[frames * Channels * sizeof(short)];
        for (var f = 0; f < frames; f++)
        {
            var value = (short)(MathF.Sin(2 * MathF.PI * frequency * f / SampleRate) * 32000);
            for (var c = 0; c < Channels; c++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(
                    pcm.AsSpan((f * Channels + c) * sizeof(short)), value);
            }
        }

        return new AudioFrame(pcm, 0, SampleRate, Channels, false);
    }

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

    [Fact]
    public void NullDependencies_Throw()
    {
        // 两个依赖都是单例且都会被跨线程读。构造期漏传的后果是运行到某个 tick 才
        // NullReference，那时已经离注册点很远。
        Assert.Throws<ArgumentNullException>(() =>
            new AudioVisualizationService(null!, new AudioSpectrumAnalyzer()));
        Assert.Throws<ArgumentNullException>(() =>
            new AudioVisualizationService(new AudioVisualizationDemand(), null!));
    }

    [Fact]
    public void Dependencies_AreExposedAsGiven()
    {
        var demand = new AudioVisualizationDemand();
        var analyzer = new AudioSpectrumAnalyzer();

        var service = new AudioVisualizationService(demand, analyzer);

        // 组件通过门面拿到需求去注册，通过门面拿到分析器读 SampleRate。
        // 若这里新建实例而非透出传入的，组件注册的需求就落在一个没人看的对象上。
        Assert.Same(demand, service.Demand);
        Assert.Same(analyzer, service.Analyzer);
    }

    [Fact]
    public void Submit_ReachesTheAnalyzer()
    {
        var (service, analyzer) = NewService();

        service.Submit(Sine(1000f, Window));

        Assert.InRange(PeakBin(analyzer.Capture().Spectrum), 41, 45);
    }

    [Fact]
    public async Task OnFrameAsync_ReachesTheAnalyzer()
    {
        var (service, analyzer) = NewService();

        await service.OnFrameAsync(Sine(5000f, Window), CancellationToken.None);

        Assert.InRange(PeakBin(analyzer.Capture().Spectrum), 211, 215);
    }

    [Fact]
    public void Capture_ForwardsToTheAnalyzer()
    {
        var (service, analyzer) = NewService();
        service.Submit(Sine(1000f, Window));

        // 同一份缓存快照，不是各算各的——否则闸门形同虚设，两条路径各触发一次 FFT。
        Assert.Same(analyzer.Capture(), service.Capture());
    }

    [Fact]
    public async Task FramesFromBothEntryPoints_ShareOneBuffer()
    {
        var (service, analyzer) = NewService();

        // 本机采集走异步 sink 入口，上游解码走同步入口。两者必须落进同一个环形缓冲，
        // 否则「先攒够半窗采集帧、再攒够半窗上游帧」会永远凑不出一整窗。
        service.Submit(Sine(1000f, Window / 2));
        await service.OnFrameAsync(Sine(1000f, Window / 2), CancellationToken.None);

        Assert.Equal(AudioSpectrumAnalyzer.SpectrumBinCount, analyzer.Capture().Spectrum.Count);
    }

    [Fact]
    public void Service_DoesNotFilterFramesByAnySourceNotion()
    {
        var (service, _) = NewService();

        // 负向契约：连续提交的帧一律照收。这一层没有「当前音源是谁」的概念，
        // 加了就等于把 MediaLink 的连接态漏进音频层，而分层约束禁止反向引用。
        service.Submit(Sine(1000f, Window));
        var first = service.Capture();

        _now += 20;
        service.Submit(Sine(5000f, Window));
        var second = service.Capture();

        Assert.InRange(PeakBin(first.Spectrum), 41, 45);
        Assert.InRange(PeakBin(second.Spectrum), 211, 215);
    }
}
