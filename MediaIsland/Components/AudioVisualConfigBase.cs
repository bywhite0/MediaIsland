using CommunityToolkit.Mvvm.ComponentModel;

namespace MediaIsland.Components;

/// <summary>设置页上的一项。每个组件用「样式 → 适用项」决定显示哪些行，不在 AXAML 里散写条件。</summary>
public enum AudioVisualSetting
{
    Style,
    Width,
    FrequencyRange,
    Decay,
    ColorSource,
    TimbreColor,
    BandCount,
    BarGap,
    Mirror
}

/// <summary>
/// 组件的颜色从哪来。按序号存进配置，只许在末尾追加。
/// </summary>
public enum AudioVisualColorSource
{
    /// <summary>ClassIsland 主题强调色，随主题切换。</summary>
    Accent,

    /// <summary>当前曲目的专辑封面主题色；没有封面或取不到颜色时回落到主题强调色。</summary>
    Cover,

    /// <summary>固定白色。</summary>
    White
}

/// <summary>
/// 四个音频组件共有的配置。回落速度也放在这里：波形组件用不到，但它的设置页不显示该项，
/// 多序列化一个字段的代价远小于再拆一层继承。
///
/// 区间旁的 decimal 镜像供 NumericUpDown 的 {x:Static} 引用——那两个属性是 decimal?，
/// 而 {x:Static} 不做字面量类型转换；镜像一律由 double 常量推导，区间只有一个真值源。
/// </summary>
public abstract class AudioVisualConfigBase : ObservableRecipient
{
    // 不叫 MinWidth / MaxWidth：那是 Avalonia 控件上真实存在的属性名。
    public const double MinComponentWidth = 16;
    public const double MaxComponentWidth = 480;
    public static readonly decimal ComponentWidthMinimum = (decimal)MinComponentWidth;
    public static readonly decimal ComponentWidthMaximum = (decimal)MaxComponentWidth;

    /// <summary>下界不取零：零意味着永不回落，包络会卡在历史峰值上。</summary>
    public const double MinDecayPerSecond = 0.2;
    public const double MaxDecayPerSecond = 20;
    public static readonly decimal DecayMinimum = (decimal)MinDecayPerSecond;
    public static readonly decimal DecayMaximum = (decimal)MaxDecayPerSecond;

    private double _width;
    private AudioVisualColorSource _colorSource = AudioVisualColorSource.Accent;
    private bool _useTimbreColor;
    private double _decayPerSecond = 2.5;

    protected AudioVisualConfigBase(double defaultWidth)
    {
        _width = Math.Clamp(defaultWidth, MinComponentWidth, MaxComponentWidth);
    }

    public double Width
    {
        get => _width;
        set => SetClamped(ref _width, value, MinComponentWidth, MaxComponentWidth);
    }

    /// <summary>每个组件各自选择：频谱和进度条摆在一起时，未必都想跟着封面变色。</summary>
    public AudioVisualColorSource ColorSource
    {
        get => _colorSource;
        set => SetProperty(ref _colorSource, value);
    }

    /// <summary>按频谱质心偏移颜色：声音越亮越暖、越沉越紫。</summary>
    public bool UseTimbreColor
    {
        get => _useTimbreColor;
        set => SetProperty(ref _useTimbreColor, value);
    }

    /// <summary>每秒衰减比例。值越大回落越快，视觉越「跳」。</summary>
    public double DecayPerSecond
    {
        get => _decayPerSecond;
        set => SetClamped(ref _decayPerSecond, value, MinDecayPerSecond, MaxDecayPerSecond);
    }

    protected void SetClamped(
        ref double field, double value, double min, double max,
        [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        var clamped = Math.Clamp(value, min, max);
        if (Math.Abs(field - clamped) < 0.01) return;
        field = clamped;
        OnPropertyChanged(name);
    }
}

/// <summary>要按频率区间取能量的组件（能量、频谱）共有的两项。</summary>
public abstract class AudioFrequencyConfigBase(double defaultWidth) : AudioVisualConfigBase(defaultWidth)
{
    /// <summary>下限上界不到 20000：下限若能顶到听觉上限，可选区间会退化成空的。</summary>
    public const double MinFrequencyLowerHz = 20;
    public const double MaxFrequencyLowerHz = 19000;
    public static readonly decimal FrequencyLowerMinimum = (decimal)MinFrequencyLowerHz;
    public static readonly decimal FrequencyLowerMaximum = (decimal)MaxFrequencyLowerHz;

    public const double MinFrequencyUpperHz = 100;
    public const double MaxFrequencyUpperHz = 20000;
    public static readonly decimal FrequencyUpperMinimum = (decimal)MinFrequencyUpperHz;
    public static readonly decimal FrequencyUpperMaximum = (decimal)MaxFrequencyUpperHz;

    private double _minFrequencyHz = 80;
    private double _maxFrequencyHz = 2000;

    public double MinFrequencyHz
    {
        get => _minFrequencyHz;
        set => SetClamped(ref _minFrequencyHz, value, MinFrequencyLowerHz, MaxFrequencyLowerHz);
    }

    public double MaxFrequencyHz
    {
        get => _maxFrequencyHz;
        set => SetClamped(ref _maxFrequencyHz, value, MinFrequencyUpperHz, MaxFrequencyUpperHz);
    }

    /// <summary>
    /// 两项各自 clamp，但相对关系没人保证——用户可以把下限拖到上限之上。
    /// 归一后仍相等时撑开 1Hz：频段映射要求下限严格小于上限，否则抛。
    /// </summary>
    public (double Min, double Max) NormalizedRange()
    {
        var min = Math.Min(MinFrequencyHz, MaxFrequencyHz);
        var max = Math.Max(MinFrequencyHz, MaxFrequencyHz);
        return min >= max ? (min, min + 1) : (min, max);
    }
}
