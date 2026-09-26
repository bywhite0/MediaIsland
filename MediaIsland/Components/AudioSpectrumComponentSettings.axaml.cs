using ClassIsland.Core.Abstractions.Controls;
using MediaIsland.Services.Audio.Visualization;

namespace MediaIsland.Components;

/// <summary>
/// 音频频谱的设置页。只对当前样式生效的行按 <see cref="AudioSpectrumConfig.ApplicableSettings"/>
/// 显隐——设置页上每一项都要对眼前的画面有作用。
/// </summary>
public partial class AudioSpectrumComponentSettings : ComponentBase<AudioSpectrumConfig>
{
    public AudioSpectrumComponentSettings(IAudioVisualSourceInfo sourceInfo, AudioVisualizationService visualization)
    {
        InitializeComponent();
        Header.Attach(sourceInfo, visualization);
        AttachedToVisualTree += (_, _) =>
        {
            Settings.PropertyChanged += OnSettingsChanged;
            ApplyVisibility();
        };
        DetachedFromVisualTree += (_, _) => Settings.PropertyChanged -= OnSettingsChanged;
    }

    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AudioSpectrumConfig.Style)) ApplyVisibility();
    }

    private void ApplyVisibility()
    {
        var applicable = AudioSpectrumConfig.ApplicableSettings(Settings.Style);
        BandCountRow.IsVisible = applicable.Contains(AudioVisualSetting.BandCount);
        BarGapRow.IsVisible = applicable.Contains(AudioVisualSetting.BarGap);
        MirrorRow.IsVisible = applicable.Contains(AudioVisualSetting.Mirror);
    }
}
