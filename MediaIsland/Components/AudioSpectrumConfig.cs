using System.Text.Json.Serialization;

namespace MediaIsland.Components;

/// <summary>音频频谱的样式。顺序即设置页下拉顺序；只许在末尾追加——配置按序号存。</summary>
public enum SpectrumStyle
{
    Capsule,
    PeakCap,
    Ridge,
    Spectrogram,
    Tri,
    Chroma
}

/// <summary>
/// 音频频谱组件的配置。频段数按样式分存：胶囊 7、细柱 16、山脊 12 各自是那种画法的
/// 合适密度，共用一个数会让切换样式时把上一种的密度带过来。
/// </summary>
public class AudioSpectrumConfig() : AudioFrequencyConfigBase(defaultWidth: 96)
{
    public const int MinBandCount = 1;
    public const int MaxBandCount = 64;
    public static readonly decimal BandCountMinimum = MinBandCount;
    public static readonly decimal BandCountMaximum = MaxBandCount;

    public const double MinBarGap = 0;
    public const double MaxBarGap = 16;
    public static readonly decimal BarGapMinimum = (decimal)MinBarGap;
    public static readonly decimal BarGapMaximum = (decimal)MaxBarGap;

    private static readonly AudioVisualSetting[] Common =
    [
        AudioVisualSetting.Style, AudioVisualSetting.Width, AudioVisualSetting.FrequencyRange,
        AudioVisualSetting.Decay, AudioVisualSetting.AccentColor, AudioVisualSetting.TimbreColor
    ];

    private SpectrumStyle _style = SpectrumStyle.Capsule;
    private int _capsuleBandCount = 7;
    private int _peakCapBandCount = 16;
    private int _ridgeBandCount = 12;
    private double _barGap = 2;
    private bool _isMirrored;

    public SpectrumStyle Style
    {
        get => _style;
        set
        {
            if (!SetProperty(ref _style, value)) return;
            OnPropertyChanged(nameof(BandCount));
        }
    }

    public int CapsuleBandCount
    {
        get => _capsuleBandCount;
        set => SetBandCount(ref _capsuleBandCount, value);
    }

    public int PeakCapBandCount
    {
        get => _peakCapBandCount;
        set => SetBandCount(ref _peakCapBandCount, value);
    }

    public int RidgeBandCount
    {
        get => _ridgeBandCount;
        set => SetBandCount(ref _ridgeBandCount, value);
    }

    /// <summary>
    /// 当前样式那一份频段数，供设置页绑定。不序列化：反序列化时它会先于 Style 被写入，
    /// 把值落进默认样式那一份里。没有频段数的样式读到的是胶囊那一份，写入被忽略。
    /// </summary>
    [JsonIgnore]
    public int BandCount
    {
        get => _style switch
        {
            SpectrumStyle.PeakCap => PeakCapBandCount,
            SpectrumStyle.Ridge => RidgeBandCount,
            _ => CapsuleBandCount
        };
        set
        {
            switch (_style)
            {
                case SpectrumStyle.Capsule: CapsuleBandCount = value; break;
                case SpectrumStyle.PeakCap: PeakCapBandCount = value; break;
                case SpectrumStyle.Ridge: RidgeBandCount = value; break;
            }
        }
    }

    public double BarGap
    {
        get => _barGap;
        set => SetClamped(ref _barGap, value, MinBarGap, MaxBarGap);
    }

    /// <summary>以水平中线上下对称展开。胶囊本身已对称，故只对细柱与山脊有意义。</summary>
    public bool IsMirrored
    {
        get => _isMirrored;
        set => SetProperty(ref _isMirrored, value);
    }

    public static IReadOnlySet<AudioVisualSetting> ApplicableSettings(SpectrumStyle style)
    {
        var set = new HashSet<AudioVisualSetting>(Common);
        if (style is SpectrumStyle.Capsule or SpectrumStyle.PeakCap or SpectrumStyle.Ridge)
            set.Add(AudioVisualSetting.BandCount);
        if (style is SpectrumStyle.Capsule or SpectrumStyle.PeakCap)
            set.Add(AudioVisualSetting.BarGap);
        if (style is SpectrumStyle.PeakCap or SpectrumStyle.Ridge)
            set.Add(AudioVisualSetting.Mirror);
        return set;
    }

    private void SetBandCount(ref int field, int value,
        [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        var clamped = Math.Clamp(value, MinBandCount, MaxBandCount);
        if (field == clamped) return;
        field = clamped;
        OnPropertyChanged(name);
        OnPropertyChanged(nameof(BandCount));
    }
}
