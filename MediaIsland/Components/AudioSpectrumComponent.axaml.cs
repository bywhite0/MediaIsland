using ClassIsland.Core.Attributes;
using MediaIsland.Controls;
using MediaIsland.Services.Audio.Visualization;

namespace MediaIsland.Components;

/// <summary>
/// 音频频谱：多频段的六种画法。GUID 沿用旧「音频频谱」，开发机岛上已放的实例不丢。
/// 峰值与声谱历史只在对应样式下推进，但切换样式时不清空——切回来能接着看。
/// </summary>
[ComponentInfo(
    "7C4E1A62-3D95-4F08-B1E7-9A2D6F83C540",
    "音频频谱",
    "",
    "以多种样式显示正在播放的声音在各频段的强弱。"
)]
// ReSharper disable once ClassNeverInstantiated.Global
public partial class AudioSpectrumComponent : AudioVisualComponentBase<AudioSpectrumConfig>
{
    /// <summary>峰值帽的下落速率（满幅/秒）。</summary>
    private const double PeakFallPerSecond = 0.6;

    /// <summary>声谱带约每秒 14 列：40 列即约 3 秒历史，与能量心电图的时间窗一致。</summary>
    private const double SpectrogramColumnSeconds = 1 / 14.0;

    private float[] _rawBands = [];
    private float[] _smoothed = [];
    private float[] _peaks = [];
    private readonly List<float[]> _history = [];
    private double _sinceLastColumn;

    public AudioSpectrumComponent(AudioVisualizationService visualization, IAudioVisualSourceInfo sourceInfo)
        : base(visualization, sourceInfo)
    {
        InitializeComponent();
    }

    protected override AudioVisualPresenterBase VisualPresenter => SpectrumPresenter;

    protected override void ApplyStaticSettings()
    {
        SpectrumPresenter.Style = Settings.Style;
        SpectrumPresenter.Mirrored = Settings.IsMirrored;
        SpectrumPresenter.BarGap = Settings.BarGap;
    }

    protected override void OnSettingChanged(string? propertyName)
    {
        // 改样式或频段数时清空包络：数组长度与含义都变了，拉伸旧数组会把低频能量带到别处。
        if (propertyName is nameof(AudioSpectrumConfig.Style) or nameof(AudioSpectrumConfig.BandCount))
        {
            _smoothed = [];
            _peaks = [];
        }
    }

    protected override void RefreshTargets(AudioVisualizationSnapshot snapshot)
    {
        var (min, max) = Settings.NormalizedRange();
        var rate = Visualization.Analyzer.SampleRate;
        _rawBands = Settings.Style switch
        {
            SpectrumStyle.Spectrogram => SpectrumBandMapper.Map(snapshot.Spectrum, rate, SpectrumGeometry.SpectrogramRows, min, max),
            SpectrumStyle.Tri => SpectrumBandMapper.Map(snapshot.Spectrum, rate, 3, min, max),
            SpectrumStyle.Chroma => AudioFeatures.ChromaFold(snapshot.Spectrum, rate, min, max),
            _ => SpectrumBandMapper.Map(snapshot.Spectrum, rate, Settings.BandCount, min, max)
        };
    }

    protected override void ClearTargets() => _rawBands = new float[_rawBands.Length];

    protected override void Advance(double deltaSeconds, double nowSeconds)
    {
        _smoothed = SpectrumEnvelope.AdvanceAll(_smoothed, _rawBands, Settings.DecayPerSecond, deltaSeconds);

        if (Settings.Style == SpectrumStyle.PeakCap)
        {
            if (_peaks.Length != _smoothed.Length) _peaks = new float[_smoothed.Length];
            for (var i = 0; i < _peaks.Length; i++)
                _peaks[i] = SpectrumEnvelope.FallLinear(_peaks[i], _smoothed[i], PeakFallPerSecond, deltaSeconds);
        }

        if (Settings.Style == SpectrumStyle.Spectrogram)
        {
            _sinceLastColumn += deltaSeconds;
            if (_sinceLastColumn >= SpectrogramColumnSeconds)
            {
                _sinceLastColumn = 0;
                _history.Add(_smoothed.ToArray());
                if (_history.Count > SpectrumGeometry.SpectrogramColumns) _history.RemoveAt(0);
            }
        }
    }

    protected override void WriteToPresenter()
    {
        SpectrumPresenter.Bands = _smoothed;
        SpectrumPresenter.Peaks = _peaks.ToArray();
        SpectrumPresenter.History = _history.ToArray();
    }

    protected override bool IsSettled()
    {
        var max = _smoothed.Length == 0 ? 0 : _smoothed.Max();
        if (max >= IdleThreshold) return false;
        if (Settings.Style == SpectrumStyle.PeakCap && _peaks.Length > 0 && _peaks.Max() >= IdleThreshold) return false;
        // 声谱带要等历史整体滚出屏幕，否则会停在一片残影上。
        return Settings.Style != SpectrumStyle.Spectrogram || _history.All(c => c.Length == 0 || c.Max() < IdleThreshold);
    }
}
