namespace MediaIsland.Components;

/// <summary>音频波形只有示波器一种画法，故只有宽度与颜色可调。</summary>
public class AudioWaveformConfig() : AudioVisualConfigBase(defaultWidth: 96)
{
    private static readonly IReadOnlySet<AudioVisualSetting> Settings = new HashSet<AudioVisualSetting>
    {
        AudioVisualSetting.Width, AudioVisualSetting.AccentColor, AudioVisualSetting.TimbreColor
    };

    public static IReadOnlySet<AudioVisualSetting> ApplicableSettings() => Settings;
}
