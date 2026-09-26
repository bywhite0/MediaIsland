namespace MediaIsland.Components;

/// <summary>音频能量的样式。只许在末尾追加——配置按序号存。</summary>
public enum EnergyStyle
{
    Orb,
    Ripple,
    GlowLine,
    Dots,
    History,
    Beat
}

/// <summary>
/// 音频能量：只看限定频段内的一个能量值。六种样式共用同一组设置——
/// 它们只在「怎样把一个数变成形状」上不同，没有哪项设置只对其中一种有意义。
/// </summary>
public class AudioEnergyConfig() : AudioFrequencyConfigBase(defaultWidth: 48)
{
    private static readonly IReadOnlySet<AudioVisualSetting> Settings = new HashSet<AudioVisualSetting>
    {
        AudioVisualSetting.Style, AudioVisualSetting.Width, AudioVisualSetting.FrequencyRange,
        AudioVisualSetting.Decay, AudioVisualSetting.AccentColor, AudioVisualSetting.TimbreColor
    };

    private EnergyStyle _style = EnergyStyle.Orb;

    public EnergyStyle Style
    {
        get => _style;
        set => SetProperty(ref _style, value);
    }

    public static IReadOnlySet<AudioVisualSetting> ApplicableSettings(EnergyStyle style) => Settings;
}
