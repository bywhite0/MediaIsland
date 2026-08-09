using Avalonia.Media;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using MediaIsland.Controls;
using MediaIsland.Services.Audio.Visualization;
using RoutedEventArgs = Avalonia.Interactivity.RoutedEventArgs;

namespace MediaIsland.Components;

/// <summary>
/// 岛内频谱组件。
///
/// 挂载即向可视化需求注册，卸载即释放——采集与上游订阅都由那个需求驱动，
/// 所以这一对必须严格成对，漏掉释放会让音频设备与约 192KB/s 的上游流量永远开着。
///
/// 定时器有两个：渲染表约 60fps，静音沉寂一秒后停表，改由 500ms 的空闲表探测。
/// 用第二个低频定时器而不是改第一个的 Interval，是为了让「已停表」这个状态在代码里
/// 显式可见，而不是藏在一个会变的周期里。
/// </summary>
[ComponentInfo(
    "7C4E1A62-3D95-4F08-B1E7-9A2D6F83C540",
    "音频频谱",
    "",
    "显示当前播放音频的实时频谱。"
)]
// ReSharper disable once ClassNeverInstantiated.Global
public partial class SpectrumComponent : ComponentBase<SpectrumComponentConfig>
{
    /// <summary>约 60fps。快于此对视觉无增益，而分析层的陈旧度闸门本就按 8ms 限流。</summary>
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(16);

    /// <summary>停表后的探测间隔。</summary>
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// 连续沉寂多久后停表。岛内组件常驻，没音乐时空转 60fps 是持续的 CPU 开销——
    /// ClassIsland 长时间挂在桌面，这类空转会真实体现在占用上。
    /// </summary>
    private static readonly TimeSpan IdleStopDelay = TimeSpan.FromSeconds(1);

    /// <summary>视觉上已无法分辨的幅度。低于它即认为包络落完了。</summary>
    private const float IdleThreshold = 0.001f;

    private readonly AudioVisualizationService _visualization;
    private readonly DispatcherTimer _renderTimer;
    private readonly DispatcherTimer _idleTimer;
    private IDisposable? _demandRegistration;

    // 平滑后的当前值（包络状态）
    private float[] _smoothedBands = [];
    private float _smoothedPulse;
    private float _smoothedRms;
    private float _smoothedPeak;

    // 最近一次快照的原始值。Revision 未变且未超时时沿用它作目标。
    private IReadOnlyList<float> _rawBands = [];
    private IReadOnlyList<float> _rawWaveform = [];
    private float _rawPulse;
    private float _rawRms;
    private float _rawPeak;

    private long _lastRevision = -1;
    private DateTime _lastDataUtc = DateTime.UtcNow;
    private DateTime? _settledSinceUtc;

    public SpectrumComponent(AudioVisualizationService visualization)
    {
        InitializeComponent();
        _visualization = visualization ?? throw new ArgumentNullException(nameof(visualization));

        // 构造里都不 Start：组件实例可能先于挂载被创建（设置页预览），
        // 那时既没有需求注册也不该有定时器在跑。
        _renderTimer = new DispatcherTimer { Interval = FrameInterval };
        _renderTimer.Tick += OnRenderTick;
        _idleTimer = new DispatcherTimer { Interval = IdlePollInterval };
        _idleTimer.Tick += OnIdleTick;
    }

    private void SpectrumComponent_OnLoaded(object sender, RoutedEventArgs e)
    {
        _demandRegistration = _visualization.Demand.Register();
        Settings.PropertyChanged += OnSettingsPropertyChanged;
        ApplyStaticSettings();
        _lastDataUtc = DateTime.UtcNow;
        _renderTimer.Start();
    }

    private void SpectrumComponent_OnUnloaded(object sender, RoutedEventArgs e)
    {
        _renderTimer.Stop();
        _idleTimer.Stop();
        Settings.PropertyChanged -= OnSettingsPropertyChanged;

        // 必须与 Loaded 成对。漏掉释放会让本机采集与上游订阅永远开着——
        // 这是本期最容易造成的资源泄漏，且不会有任何报错。
        _demandRegistration?.Dispose();
        _demandRegistration = null;
    }

    private void OnSettingsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // 改频段数时清空包络，让它从零重建而不是拉伸旧数组——
        // 拉伸会把旧的低频能量带到新增的高频段上。
        if (e.PropertyName == nameof(SpectrumComponentConfig.BandCount))
        {
            _smoothedBands = [];
        }

        ApplyStaticSettings();

        // 改了配置就当作有新数据：否则若此时正处于停表状态，改形态要等到下次
        // 有声音才生效，看起来像设置没保存。
        if (!_renderTimer.IsEnabled)
        {
            _idleTimer.Stop();
            _lastDataUtc = DateTime.UtcNow;
            _renderTimer.Start();
        }
    }

    /// <summary>把不需要逐帧计算的配置项直接写进 presenter。</summary>
    private void ApplyStaticSettings()
    {
        Presenter.Mode = Settings.Mode;
        Presenter.Mirrored = Settings.IsMirrored;
        Presenter.BarGap = Settings.BarGap;
        Presenter.BarCornerRadius = Settings.BarCornerRadius;
        Presenter.Width = Settings.Width;
        ApplyForeground();
    }

    /// <summary>
    /// 与既有进度条同构：要主题强调色时清除本地值，让继承链把主题色送下来；
    /// 要固定色时显式赋值。自己去查资源键会在主题换代时静默失效。
    /// </summary>
    private void ApplyForeground()
    {
        if (Settings.UseAccentColor)
        {
            Presenter.ClearValue(SpectrumPresenter.ForegroundProperty);
            return;
        }

        Presenter.Foreground = Brushes.White;
    }

    /// <summary>
    /// 关键：Revision 只决定是否刷新目标值，包络每帧都推进。
    ///
    /// 不可在 Revision 未变时直接 return——分析器在写位置未推进时返回缓存快照，
    /// 据此早退会让上游断连后最后一帧的频谱永远停在屏幕上。那与「静音跳过发送会让
    /// 接收端 FFT 冻结」是同一个失效形态，只是成因从「不发帧」换成了「不刷新」。
    /// </summary>
    private void OnRenderTick(object? sender, EventArgs e)
    {
        var snapshot = _visualization.Capture();
        var now = DateTime.UtcNow;

        if (snapshot.Revision != _lastRevision)
        {
            _lastRevision = snapshot.Revision;
            _lastDataUtc = now;
            RefreshTargets(snapshot);
        }
        else if (SpectrumEnvelope.IsStalled(now - _lastDataUtc))
        {
            ClearTargets();
        }

        AdvanceEnvelope();
        WriteToPresenter();
        UpdateIdleState(now);
    }

    private void RefreshTargets(AudioVisualizationSnapshot snapshot)
    {
        var (min, max) = FrequencyRange();
        var sampleRate = _visualization.Analyzer.SampleRate;

        _rawBands = SpectrumBandMapper.Map(snapshot.Spectrum, sampleRate, Settings.BandCount, min, max);
        _rawPulse = SpectrumBandMapper.Energy(snapshot.Spectrum, sampleRate, min, max);
        _rawRms = snapshot.Rms;
        _rawPeak = snapshot.Peak;
        _rawWaveform = snapshot.Waveform;
    }

    /// <summary>
    /// 两个频率配置项各自 clamp，但相对关系没人保证——用户可以把下限拖到上限之上。
    /// 归一后仍相等时撑开 1Hz：映射器要求下限严格小于上限，否则抛。
    /// </summary>
    private (double Min, double Max) FrequencyRange()
    {
        var min = Math.Min(Settings.MinFrequencyHz, Settings.MaxFrequencyHz);
        var max = Math.Max(Settings.MinFrequencyHz, Settings.MaxFrequencyHz);
        return min >= max ? (min, min + 1) : (min, max);
    }

    private void ClearTargets()
    {
        _rawBands = [];
        _rawPulse = 0f;
        _rawRms = 0f;
        _rawPeak = 0f;
        // 波形不走包络，帧流停时直接给空数组——presenter 见点数少于 2 即不绘制。
        _rawWaveform = [];
    }

    private void AdvanceEnvelope()
    {
        var decay = Settings.DecayPerSecond;
        var delta = FrameInterval.TotalSeconds;

        _smoothedBands = SpectrumEnvelope.AdvanceAll(_smoothedBands, _rawBands, decay, delta);
        _smoothedPulse = SpectrumEnvelope.Advance(_smoothedPulse, _rawPulse, decay, delta);
        _smoothedRms = SpectrumEnvelope.Advance(_smoothedRms, _rawRms, decay, delta);
        _smoothedPeak = SpectrumEnvelope.Advance(_smoothedPeak, _rawPeak, decay, delta);
    }

    private void WriteToPresenter()
    {
        Presenter.Bands = _smoothedBands;
        Presenter.PulseEnergy = _smoothedPulse;
        Presenter.Rms = _smoothedRms;
        Presenter.Peak = _smoothedPeak;
        // 波形是时域信号，平滑会把它抹平，故直接透传原始值。
        Presenter.Waveform = _rawWaveform;
    }

    /// <summary>
    /// 停表判定放在最后：要等包络落完再停，否则会停在半衰减的画面上。
    /// </summary>
    private void UpdateIdleState(DateTime now)
    {
        var settled = _smoothedPulse < IdleThreshold
                      && _smoothedRms < IdleThreshold
                      && (_smoothedBands.Length == 0 || _smoothedBands.Max() < IdleThreshold);

        if (!settled)
        {
            _settledSinceUtc = null;
            return;
        }

        _settledSinceUtc ??= now;
        if (now - _settledSinceUtc < IdleStopDelay)
        {
            return;
        }

        _renderTimer.Stop();
        _idleTimer.Start();
        _settledSinceUtc = null;
    }

    /// <summary>
    /// 用 Revision 而非 IsSilent 判断数据回来了没：帧流完全中断时 IsSilent 会停在
    /// 中断前的值上，靠它复启会漏醒；Revision 递增是「确有新数据」的唯一可靠信号。
    /// </summary>
    private void OnIdleTick(object? sender, EventArgs e)
    {
        if (_visualization.Capture().Revision == _lastRevision)
        {
            return;
        }

        _idleTimer.Stop();
        _lastDataUtc = DateTime.UtcNow;
        _renderTimer.Start();
    }
}
