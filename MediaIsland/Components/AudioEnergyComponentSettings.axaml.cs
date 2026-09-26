using ClassIsland.Core.Abstractions.Controls;
using MediaIsland.Services.Audio.Visualization;

namespace MediaIsland.Components;

/// <summary>音频能量的设置页。六种样式共用同一组设置，无需按样式显隐。</summary>
public partial class AudioEnergyComponentSettings : ComponentBase<AudioEnergyConfig>
{
    public AudioEnergyComponentSettings(IAudioVisualSourceInfo sourceInfo, AudioVisualizationService visualization)
    {
        InitializeComponent();
        Header.Attach(sourceInfo, visualization);
    }
}
