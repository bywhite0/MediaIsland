using ClassIsland.Core.Abstractions.Controls;
using MediaIsland.Services.Audio.Visualization;

namespace MediaIsland.Components;

public partial class AudioWaveformComponentSettings : ComponentBase<AudioWaveformConfig>
{
    public AudioWaveformComponentSettings(IAudioVisualSourceInfo sourceInfo, AudioVisualizationService visualization)
    {
        InitializeComponent();
        Header.Attach(sourceInfo, visualization);
    }
}
