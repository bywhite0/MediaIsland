using MediaIsland.Services.Audio.Visualization.Fft;

namespace MediaIsland.Services.Audio.Visualization;

/// <summary>
/// PCM → 可视化快照。
///
/// FFT 不在音频线程上跑。<see cref="OnFrameAsync"/> 只做混单声道与拷贝（几微秒），
/// FFT 在 <see cref="Capture"/> 里、由 UI 的定时器触发。理由有二：
///
/// 1. FFT 的正确触发源是「要显示了」而非「数据到了」。采集实测约 99 帧/秒而渲染 60fps，
///    按数据驱动会有约 40% 的结果在被人看见前就被覆盖，纯浪费；而那 10ms 延迟差不可察觉。
/// 2. <see cref="IAudioFrameSource.FrameAvailable"/> 的契约明写「不得阻塞，阻塞会让 WASAPI
///    缓冲溢出并产生丢帧」。2048 点 FFT 放在那条路径上是明确的风险且买不到可见收益。
///
/// <see cref="Capture"/> 带陈旧度闸门，故 N 个频谱组件各自轮询时实际只算一遍——
/// 这是选共享分析器而非组件自持分析器的主要收益。
///
/// 输出定长原始幅度谱而非分好的频段：段数与频率范围是每个组件各自的显示口味，
/// 放在这里会让单例把一个组件的配置强加给另一个。分析层只做贵的、与观察者无关的部分。
/// </summary>
public sealed class AudioSpectrumAnalyzer : IAudioFrameSink
{
    public const int WindowSize = 2048;

    /// <summary>实信号的谱共轭对称，后半无信息。恒定，不随任何配置变化。</summary>
    public const int SpectrumBinCount = WindowSize / 2;

    /// <summary>示波器的抽样点数。远小于窗长——岛内宽度只有几百像素，多了也画不出。</summary>
    private const int WaveformPoints = 128;

    /// <summary>两次实算的最小间隔。60fps 的周期约 16.7ms，取 8ms 留出余量而不至于每帧都算两遍。</summary>
    private const long MinRecomputeIntervalMs = 8;

    private readonly AudioSampleRing _ring = new(WindowSize * 2);
    private readonly Func<long> _nowMs;
    private readonly object _captureGate = new();
    private readonly float[] _real = new float[WindowSize];
    private readonly float[] _imaginary = new float[WindowSize];

    private int _sampleRate = 48000;
    private long _lastComputedAtMs;
    private long _lastWrittenCount = -1;
    private long _revision;

    /// <summary>
    /// 是否已经实算过至少一次。用显式布尔而不是给 <see cref="_lastComputedAtMs"/> 一个
    /// 「很久以前」的哨兵初值：哨兵要参与减法，而 <c>now - long.MinValue</c> 在 long 上
    /// unchecked 溢出后环绕成负数，于是间隔恒小于闸门——分析器在第一帧就卡死，
    /// 永远返回空快照，且因为走的是早退分支而不更新时间戳，自己也出不来。
    /// 这类缺陷不抛异常、不打日志，只表现为「频谱一直不出来」。
    /// </summary>
    private bool _hasComputed;

    private volatile bool _lastFrameSilent = true;
    private AudioVisualizationSnapshot _cached = AudioVisualizationSnapshot.Empty;

    public AudioSpectrumAnalyzer(Func<long>? nowMsProvider = null) =>
        _nowMs = nowMsProvider ?? (() => Environment.TickCount64);

    /// <summary>最近一帧的采样率。组件侧据此把 bin 下标换算成频率。</summary>
    public int SampleRate => Volatile.Read(ref _sampleRate);

    /// <summary>音频线程入口：只拷贝，不计算。</summary>
    public ValueTask OnFrameAsync(AudioFrame frame, CancellationToken cancellationToken)
    {
        Submit(frame);
        return ValueTask.CompletedTask;
    }

    public void Submit(AudioFrame frame)
    {
        if (frame.Pcm.Length == 0 || frame.Channels <= 0)
        {
            return;
        }

        if (frame.SampleRate > 0)
        {
            Volatile.Write(ref _sampleRate, frame.SampleRate);
        }

        _lastFrameSilent = frame.IsSilent;
        _ring.Append(frame.Pcm, frame.Channels);
    }

    /// <summary>
    /// UI 线程入口。闸门有两道：写位置未推进（没有新数据），或距上次实算不足
    /// <see cref="MinRecomputeIntervalMs"/>。任一命中即返回上次的快照，
    /// 其 <see cref="AudioVisualizationSnapshot.Revision"/> 不变，调用方可据此跳过重绘。
    /// </summary>
    public AudioVisualizationSnapshot Capture()
    {
        lock (_captureGate)
        {
            var written = _ring.WrittenCount;
            var now = _nowMs();

            // 第一次实算没有「上一次」可比，闸门不适用——它限的是两次实算之间的间隔。
            if (written == _lastWrittenCount ||
                (_hasComputed && now - _lastComputedAtMs < MinRecomputeIntervalMs))
            {
                return _cached;
            }

            var window = _real.AsSpan();
            if (!_ring.TryReadLatest(window))
            {
                // 不足一窗：返回空快照而非半窗垃圾频谱。
                // 刻意不置 _hasComputed——这次没算，下次攒够了要能立刻算而不必再等一个闸门周期。
                _lastWrittenCount = written;
                return _cached = AudioVisualizationSnapshot.Empty;
            }

            var waveform = SampleWaveform(window);
            var (rms, peak) = ComputeLevels(window);

            RealFft.ApplyHannWindow(window);
            _imaginary.AsSpan().Clear();
            RealFft.Transform(window, _imaginary);

            // 每次新建：快照对外是不可变的，复用内部数组会让上一份快照被悄悄改写。
            var spectrum = new float[SpectrumBinCount];
            RealFft.Magnitudes(window, _imaginary, spectrum);

            // 归一：Hann 窗令相干增益减半，故除以 WindowSize/4 而非 WindowSize/2。
            for (var i = 0; i < spectrum.Length; i++)
            {
                spectrum[i] = Math.Clamp(spectrum[i] / (WindowSize / 4f), 0f, 1f);
            }

            _lastWrittenCount = written;
            _lastComputedAtMs = now;
            _hasComputed = true;
            return _cached = new AudioVisualizationSnapshot
            {
                Spectrum = spectrum,
                Waveform = waveform,
                Rms = rms,
                Peak = peak,
                IsSilent = _lastFrameSilent,
                Revision = ++_revision
            };
        }
    }

    /// <summary>
    /// 抽样必须在加窗之前：Hann 窗把首尾压到 0，加窗后再抽样，
    /// 示波器会显示两头收窄的假象——那是分析器的加工痕迹，不是信号本身。
    /// </summary>
    private static float[] SampleWaveform(ReadOnlySpan<float> window)
    {
        var points = new float[WaveformPoints];
        var stride = window.Length / WaveformPoints;
        for (var i = 0; i < WaveformPoints; i++)
        {
            points[i] = window[i * stride];
        }

        return points;
    }

    private static (float Rms, float Peak) ComputeLevels(ReadOnlySpan<float> window)
    {
        var sumSquares = 0d;
        var peak = 0f;
        foreach (var sample in window)
        {
            sumSquares += sample * (double)sample;
            var magnitude = MathF.Abs(sample);
            if (magnitude > peak)
            {
                peak = magnitude;
            }
        }

        return ((float)Math.Sqrt(sumSquares / window.Length), Math.Min(peak, 1f));
    }
}
