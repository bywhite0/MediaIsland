using ClassIsland.Core.Attributes;
using MediaIsland.Controls;
using MediaIsland.Services.Audio.Visualization;
using MediaIsland.Services.Media;

namespace MediaIsland.Components;

/// <summary>音频电平：电平表与声像光点。仪表语义——快起慢落，如实报告，不按频段取能量。</summary>
[ComponentInfo(
    "D41A6E93-8C2B-4F75-9B0E-7A3F5E1C2D84",
    "音频电平",
    "",
    "显示正在播放的声音的音量与左右声像。"
)]
// ReSharper disable once ClassNeverInstantiated.Global
public partial class AudioLevelComponent : AudioVisualComponentBase<AudioLevelConfig>
{
    private float _rawRms, _rawPeak, _rawLeft, _rawRight;
    private float _rms, _peak, _left, _right;

    public AudioLevelComponent(
        AudioVisualizationService visualization,
        IAudioVisualSourceInfo sourceInfo,
        CoverColorService coverColors)
        : base(visualization, sourceInfo, coverColors)
    {
        InitializeComponent();
    }

    protected override AudioVisualPresenterBase VisualPresenter => LevelPresenter;

    protected override void ApplyStaticSettings() => LevelPresenter.Style = Settings.Style;

    protected override void RefreshTargets(AudioVisualizationSnapshot snapshot)
    {
        (_rawRms, _rawPeak) = (snapshot.Rms, snapshot.Peak);
        (_rawLeft, _rawRight) = (snapshot.LeftRms, snapshot.RightRms);
    }

    protected override void ClearTargets() => _rawRms = _rawPeak = _rawLeft = _rawRight = 0;

    protected override void Advance(double deltaSeconds, double nowSeconds)
    {
        var decay = Settings.DecayPerSecond;
        _rms = SpectrumEnvelope.Advance(_rms, _rawRms, decay, deltaSeconds);
        _peak = SpectrumEnvelope.Advance(_peak, _rawPeak, decay, deltaSeconds);
        _left = SpectrumEnvelope.Advance(_left, _rawLeft, decay, deltaSeconds);
        _right = SpectrumEnvelope.Advance(_right, _rawRight, decay, deltaSeconds);
    }

    protected override void WriteToPresenter()
    {
        LevelPresenter.Rms = _rms;
        LevelPresenter.Peak = _peak;
        LevelPresenter.Left = _left;
        LevelPresenter.Right = _right;
    }

    protected override bool IsSettled() =>
        _rms < IdleThreshold && _peak < IdleThreshold && _left < IdleThreshold && _right < IdleThreshold;
}
