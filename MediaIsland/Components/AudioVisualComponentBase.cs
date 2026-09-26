using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Reactive;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using MediaIsland.Controls;
using MediaIsland.Services.Audio.Visualization;
using MediaIsland.Services.Media;

namespace MediaIsland.Components;

/// <summary>
/// 四个音频组件的共同骨架，从原频谱组件原样抽出。
///
/// 挂载即向可视化需求注册，卸载即释放——采集与上游订阅都由那个需求驱动，漏掉释放会让
/// 音频设备与约 192KB/s 的上游流量永远开着。
///
/// 两个定时器：渲染表约 60fps，沉寂一秒后停表，改由 500ms 的空闲表探测。用第二个低频
/// 定时器而不是改第一个的 Interval，是为了让「已停表」这个状态在代码里显式可见。
///
/// 只能有一个泛型参数且必须是配置类型：ClassIsland 取组件 BaseType 的第一个泛型参数
/// 作为配置类型去反序列化，多一个参数或换顺序，配置就会被当成别的类型加载。
/// </summary>
public abstract class AudioVisualComponentBase<TConfig> : ComponentBase<TConfig>
    where TConfig : AudioVisualConfigBase
{
    /// <summary>约 60fps。分析层的陈旧度闸门本就按 8ms 限流，更快无视觉增益。</summary>
    protected static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(16);

    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan IdleStopDelay = TimeSpan.FromSeconds(1);

    /// <summary>视觉上已无法分辨的幅度。低于它即认为落完了。</summary>
    protected const float IdleThreshold = 0.001f;

    /// <summary>质心的平滑速率（每秒趋近比例）。颜色跟得太紧会随每个鼓点闪烁。</summary>
    private const double CentroidFollowPerSecond = 4;

    /// <summary>FluentAvalonia 的 ProgressBar 默认前景用的键，ClassIsland 主题同样提供。</summary>
    private const string AccentBrushKey = "AccentFillColorDefaultBrush";

    private readonly IAudioVisualSourceInfo _sourceInfo;
    private readonly CoverColorService _coverColors;
    private readonly DispatcherTimer _renderTimer;
    private readonly DispatcherTimer _idleTimer;

    /// <summary>presenter 始终用这一支笔刷，颜色变化只改它的 Color——不在绑定与本地值之间来回切换。</summary>
    private readonly SolidColorBrush _foreground = new(Colors.White);
    private IDisposable? _demandRegistration;
    private long _lastRevision = -1;
    private DateTime _lastDataUtc = DateTime.UtcNow;
    private DateTime? _settledSinceUtc;
    private float _rawCentroid = 0.5f;
    private double _centroid = 0.5;
    private Color _baseColor = Colors.White;
    private Color? _accentColor;
    private IDisposable? _accentSubscription;
    private IDisposable? _coverRegistration;

    protected AudioVisualComponentBase(
        AudioVisualizationService visualization,
        IAudioVisualSourceInfo sourceInfo,
        CoverColorService coverColors)
    {
        Visualization = visualization ?? throw new ArgumentNullException(nameof(visualization));
        _sourceInfo = sourceInfo ?? throw new ArgumentNullException(nameof(sourceInfo));
        _coverColors = coverColors ?? throw new ArgumentNullException(nameof(coverColors));

        // 构造里都不 Start：组件实例可能先于挂载被创建（设置页预览）。
        _renderTimer = new DispatcherTimer { Interval = FrameInterval };
        _renderTimer.Tick += OnRenderTick;
        _idleTimer = new DispatcherTimer { Interval = IdlePollInterval };
        _idleTimer.Tick += OnIdleTick;

        Loaded += OnLoadedCore;
        Unloaded += OnUnloadedCore;
    }

    protected AudioVisualizationService Visualization { get; }

    protected abstract AudioVisualPresenterBase VisualPresenter { get; }

    /// <summary>快照有新 Revision 时刷新目标值。</summary>
    protected abstract void RefreshTargets(AudioVisualizationSnapshot snapshot);

    /// <summary>帧流中断：把目标拉到零，让包络自然落下。</summary>
    protected abstract void ClearTargets();

    /// <summary>每帧推进包络与随时间变化的状态（涟漪、滚动历史）。</summary>
    protected abstract void Advance(double deltaSeconds, double nowSeconds);

    protected abstract void WriteToPresenter();

    /// <summary>画面已落到静止形态、可以停表。滚动类样式要等历史全部滚出才算。</summary>
    protected abstract bool IsSettled();

    /// <summary>不需要逐帧计算的配置项直接写进 presenter。</summary>
    protected abstract void ApplyStaticSettings();

    /// <summary>某项配置变了。默认无事可做；需要清状态的子类覆写。</summary>
    protected virtual void OnSettingChanged(string? propertyName)
    {
    }

    private void OnLoadedCore(object? sender, RoutedEventArgs e)
    {
        _demandRegistration = Visualization.Demand.Register();
        Settings.PropertyChanged += OnSettingsPropertyChanged;
        VisualPresenter.Width = Settings.Width;
        VisualPresenter.Foreground = _foreground;
        // 主题色始终订阅：它既是「主题强调色」本身，也是封面取不到时的回落。
        _accentSubscription = VisualPresenter.GetResourceObservable(AccentBrushKey)
            .Subscribe(new AnonymousObserver<object?>(OnAccentBrushChanged));
        ApplyColorSource();
        ApplyStaticSettings();
        UpdateStatus(hasRecentData: false);
        _lastDataUtc = DateTime.UtcNow;
        _renderTimer.Start();
    }

    private void OnUnloadedCore(object? sender, RoutedEventArgs e)
    {
        _renderTimer.Stop();
        _idleTimer.Stop();
        Settings.PropertyChanged -= OnSettingsPropertyChanged;
        _accentSubscription?.Dispose();
        _accentSubscription = null;
        ReleaseCover();

        // 必须与 Loaded 成对，且无条件——停表状态下被移出岛同样要释放。
        _demandRegistration?.Dispose();
        _demandRegistration = null;
    }

    private void OnSettingsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        OnSettingChanged(e.PropertyName);
        VisualPresenter.Width = Settings.Width;
        ApplyColorSource();
        ApplyStaticSettings();

        // 改了配置就当作有新数据：停表状态下改样式要立刻看到。
        if (!_renderTimer.IsEnabled)
        {
            _idleTimer.Stop();
            _lastDataUtc = DateTime.UtcNow;
            _renderTimer.Start();
        }
    }

    /// <summary>
    /// 选了封面色才向封面取色服务登记需求——没人选时服务不订阅媒体、不做任何提取。
    /// 登记与撤销都跟着配置走，改完颜色来源立刻生效。
    /// </summary>
    private void ApplyColorSource()
    {
        var wantsCover = Settings.ColorSource == AudioVisualColorSource.Cover;
        if (wantsCover && _coverRegistration is null)
        {
            _coverColors.ColorChanged += UpdateBaseColor;
            _coverRegistration = _coverColors.Register();
        }
        else if (!wantsCover)
        {
            ReleaseCover();
        }

        UpdateBaseColor();
    }

    private void ReleaseCover()
    {
        if (_coverRegistration is null) return;
        _coverColors.ColorChanged -= UpdateBaseColor;
        _coverRegistration.Dispose();
        _coverRegistration = null;
    }

    private void OnAccentBrushChanged(object? value)
    {
        _accentColor = (value as ISolidColorBrush)?.Color;
        UpdateBaseColor();
    }

    /// <summary>
    /// 基色的任一输入变了（颜色来源、主题、封面）都走这里。停表时画面不会自己重绘，
    /// 所以要显式失效一次，否则换主题或换歌后颜色要等到下次有声音才更新。
    /// </summary>
    private void UpdateBaseColor()
    {
        var cover = _coverRegistration is null ? null : _coverColors.CurrentColor;
        _baseColor = AudioVisualColorRules.ResolveBase(Settings.ColorSource, cover, _accentColor);
        UpdateForeground();
        VisualPresenter.InvalidateVisual();
    }

    private void UpdateForeground() =>
        _foreground.Color = AudioVisualColorRules.Foreground(_baseColor, Settings.UseTimbreColor, _centroid);

    /// <summary>
    /// Revision 只决定是否刷新目标值，包络每帧都推进。不可在 Revision 未变时直接 return——
    /// 分析器在写位置未推进时返回缓存快照，据此早退会让最后一帧永远停在屏幕上。
    /// </summary>
    private void OnRenderTick(object? sender, EventArgs e)
    {
        var snapshot = Visualization.Capture();
        var now = DateTime.UtcNow;
        var stalled = false;

        if (snapshot.Revision != _lastRevision)
        {
            _lastRevision = snapshot.Revision;
            _lastDataUtc = now;
            _rawCentroid = AudioFeatures.SpectralCentroid(
                snapshot.Spectrum, Visualization.Analyzer.SampleRate, 20, 20000);
            RefreshTargets(snapshot);
        }
        else if (SpectrumEnvelope.IsStalled(now - _lastDataUtc))
        {
            stalled = true;
            _rawCentroid = 0.5f;
            ClearTargets();
        }

        var delta = FrameInterval.TotalSeconds;
        Advance(delta, (now - DateTime.UnixEpoch).TotalSeconds);
        AdvanceTimbre(delta);
        WriteToPresenter();

        var settled = IsSettled();
        UpdateStatus(hasRecentData: !stalled && !settled);
        UpdateIdleState(now, settled);
    }

    private void AdvanceTimbre(double delta)
    {
        if (!Settings.UseTimbreColor) return;
        _centroid += (_rawCentroid - _centroid) * Math.Min(1, delta * CentroidFollowPerSecond);
        UpdateForeground();
    }

    private void UpdateStatus(bool hasRecentData)
    {
        var status = AudioVisualStatusRules.Resolve(
            _sourceInfo.IsLocalSourceAvailable, _sourceInfo.IsConsumingUpstream, hasRecentData);
        VisualPresenter.Opacity = AudioVisualStatusRules.OpacityFor(status);
    }

    /// <summary>停表判定放在最后：要等画面落完再停，否则会停在半衰减的画面上。</summary>
    private void UpdateIdleState(DateTime now, bool settled)
    {
        if (!settled)
        {
            _settledSinceUtc = null;
            return;
        }

        _settledSinceUtc ??= now;
        if (now - _settledSinceUtc < IdleStopDelay) return;

        _renderTimer.Stop();
        _idleTimer.Start();
        _settledSinceUtc = null;
    }

    /// <summary>
    /// 用 Revision 而非 IsSilent 判断数据回来了没：帧流完全中断时 IsSilent 会停在中断前的值上。
    /// 空闲时也刷新状态：采集源在停表期间变得可用/不可用，岛上的明暗要跟着变。
    /// </summary>
    private void OnIdleTick(object? sender, EventArgs e)
    {
        UpdateStatus(hasRecentData: false);
        if (Visualization.Capture().Revision == _lastRevision) return;

        _idleTimer.Stop();
        _lastDataUtc = DateTime.UtcNow;
        _renderTimer.Start();
    }
}
