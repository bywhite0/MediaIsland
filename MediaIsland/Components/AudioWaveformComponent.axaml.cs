using ClassIsland.Core.Attributes;
using MediaIsland.Controls;
using MediaIsland.Services.Audio.Visualization;
using MediaIsland.Services.Media;

namespace MediaIsland.Components;

/// <summary>
/// 音频波形：示波器。波形是时域信号，平滑会把它抹平，故不走包络、直接透传；
/// 停表判据借 RMS 的包络——它才能说明「声音已经落完了」。
/// </summary>
[ComponentInfo(
    "9E2C7A48-1F5D-4B36-A0E8-5D4B1C9F7E02",
    "音频波形",
    "",
    "以示波器显示正在播放的声音波形。"
)]
// ReSharper disable once ClassNeverInstantiated.Global
public partial class AudioWaveformComponent : AudioVisualComponentBase<AudioWaveformConfig>
{
    private IReadOnlyList<float> _waveform = [];
    private float _rawRms;
    private float _smoothedRms;

    public AudioWaveformComponent(
        AudioVisualizationService visualization,
        IAudioVisualSourceInfo sourceInfo,
        CoverColorService coverColors)
        : base(visualization, sourceInfo, coverColors)
    {
        InitializeComponent();
    }

    protected override AudioVisualPresenterBase VisualPresenter => WaveformPresenter;

    protected override void ApplyStaticSettings()
    {
    }

    protected override void RefreshTargets(AudioVisualizationSnapshot snapshot)
    {
        _waveform = snapshot.Waveform;
        _rawRms = snapshot.Rms;
    }

    protected override void ClearTargets()
    {
        _waveform = [];
        _rawRms = 0;
    }

    protected override void Advance(double deltaSeconds, double nowSeconds) =>
        _smoothedRms = SpectrumEnvelope.Advance(_smoothedRms, _rawRms, Settings.DecayPerSecond, deltaSeconds);

    protected override void WriteToPresenter() => WaveformPresenter.Waveform = _waveform;

    protected override bool IsSettled() => _smoothedRms < IdleThreshold;
}
