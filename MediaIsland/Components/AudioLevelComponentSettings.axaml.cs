using ClassIsland.Core.Abstractions.Controls;
using MediaIsland.Services.Audio.Visualization;

namespace MediaIsland.Components;

public partial class AudioLevelComponentSettings : ComponentBase<AudioLevelConfig>
{
    public AudioLevelComponentSettings(IAudioVisualSourceInfo sourceInfo, AudioVisualizationService visualization)
    {
        InitializeComponent();
        Header.Attach(sourceInfo, visualization);
    }
}
