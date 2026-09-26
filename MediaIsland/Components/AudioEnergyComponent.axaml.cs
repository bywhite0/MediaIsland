using ClassIsland.Core.Attributes;
using MediaIsland.Controls;
using MediaIsland.Services.Audio.Visualization;
using MediaIsland.Services.Media;

namespace MediaIsland.Components;

/// <summary>
/// 音频能量：限定频段内的一个能量值，六种画法。起拍检测只在涟漪与四拍计数下推进，
/// 但切换样式时一并清空——换到节拍类样式时不该带着旧的起拍状态。
/// </summary>
[ComponentInfo(
    "3B8F5D21-6A4C-4E9B-8D17-C2E5A9F04B63",
    "音频能量",
    "\uEE21",
    "以一个随声音强弱变化的图形，显示正在播放的声音。"
)]
// ReSharper disable once ClassNeverInstantiated.Global
public partial class AudioEnergyComponent : AudioVisualComponentBase<AudioEnergyConfig>
{
    /// <summary>节拍光晕的熄灭速率（每秒）。</summary>
    private const double BeatGlowFallPerSecond = 3;

    private readonly OnsetDetector _onsets = new();
    private readonly List<RippleRing> _rings = [];
    private readonly List<float> _history = [];
    private IReadOnlyList<float> _latestSpectrum = [];
    private bool _hasNewSpectrum;
    private float _raw;
    private float _smoothed;
    private int _beatIndex = -1;
    private double _beatGlow;
    private double _phase;

    public AudioEnergyComponent(
        AudioVisualizationService visualization,
        IAudioVisualSourceInfo sourceInfo,
        CoverColorService coverColors)
        : base(visualization, sourceInfo, coverColors)
    {
        InitializeComponent();
    }

    protected override AudioVisualPresenterBase VisualPresenter => EnergyPresenter;

    protected override void ApplyStaticSettings() => EnergyPresenter.Style = Settings.Style;

    protected override void OnSettingChanged(string? propertyName)
    {
        if (propertyName != nameof(AudioEnergyConfig.Style)) return;
        _onsets.Reset();
        _rings.Clear();
        _history.Clear();
        _beatIndex = -1;
        _beatGlow = 0;
    }

    protected override void RefreshTargets(AudioVisualizationSnapshot snapshot)
    {
        var (min, max) = Settings.NormalizedRange();
        _raw = SpectrumBandMapper.Energy(snapshot.Spectrum, Visualization.Analyzer.SampleRate, min, max);
        _latestSpectrum = snapshot.Spectrum;
        _hasNewSpectrum = true;
    }

    protected override void ClearTargets()
    {
        _raw = 0;
        _latestSpectrum = [];
    }

    protected override void Advance(double deltaSeconds, double nowSeconds)
    {
        _smoothed = SpectrumEnvelope.Advance(_smoothed, _raw, Settings.DecayPerSecond, deltaSeconds);
        _phase += deltaSeconds;

        // 只在有新谱时喂检测器：同一份谱喂两次，第二次通量为零，会把滑动均值往下拖。
        if (Settings.Style is EnergyStyle.Ripple or EnergyStyle.Beat && _hasNewSpectrum)
        {
            _hasNewSpectrum = false;
            if (_onsets.Update(_latestSpectrum, nowSeconds))
            {
                _rings.Add(new RippleRing(0, _smoothed));
                _beatIndex = (_beatIndex + 1) % 4;
                _beatGlow = 1;
            }
        }

        for (var k = _rings.Count - 1; k >= 0; k--)
        {
            var age = _rings[k].Age01 + deltaSeconds / EnergyGeometry.RippleLifetimeSeconds;
            if (age >= 1) _rings.RemoveAt(k);
            else _rings[k] = _rings[k] with { Age01 = age };
        }

        _beatGlow = Math.Max(0, _beatGlow - deltaSeconds * BeatGlowFallPerSecond);

        if (Settings.Style == EnergyStyle.History)
        {
            _history.Add(_smoothed);
            if (_history.Count > EnergyGeometry.HistoryCapacity) _history.RemoveAt(0);
        }
    }

    protected override void WriteToPresenter()
    {
        EnergyPresenter.Energy = _smoothed;
        EnergyPresenter.PhaseSeconds = _phase;
        EnergyPresenter.Rings = _rings.ToArray();
        EnergyPresenter.History = _history.ToArray();
        EnergyPresenter.BeatIndex = _beatGlow > 0 ? _beatIndex : -1;
        EnergyPresenter.BeatGlow = _beatGlow;
    }

    protected override bool IsSettled() =>
        _smoothed < IdleThreshold
        && _rings.Count == 0
        && _beatGlow <= 0
        // 心电图要等尖峰滚出左缘，否则会停在一串残影上。
        && (Settings.Style != EnergyStyle.History || _history.All(v => v < IdleThreshold));
}
