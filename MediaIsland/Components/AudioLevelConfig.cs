namespace MediaIsland.Components;

/// <summary>音频电平的样式。只许在末尾追加——配置按序号存。</summary>
public enum LevelStyle
{
    Meter,
    Pan
}

/// <summary>音频电平：仪表类，如实报告音量与声像，不按频段取能量。</summary>
public class AudioLevelConfig() : AudioVisualConfigBase(defaultWidth: 80)
{
    private static readonly IReadOnlySet<AudioVisualSetting> Settings = new HashSet<AudioVisualSetting>
    {
        AudioVisualSetting.Style, AudioVisualSetting.Width, AudioVisualSetting.Decay,
        AudioVisualSetting.AccentColor, AudioVisualSetting.TimbreColor
    };

    private LevelStyle _style = LevelStyle.Meter;

    public LevelStyle Style
    {
        get => _style;
        set => SetProperty(ref _style, value);
    }

    public static IReadOnlySet<AudioVisualSetting> ApplicableSettings(LevelStyle style) => Settings;
}
