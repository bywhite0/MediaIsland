using CommunityToolkit.Mvvm.ComponentModel;
using MediaIsland.Controls;

namespace MediaIsland.Components
{
    /// <summary>
    /// 频谱组件的配置。全部项都是本组件私有的——放两个频谱组件可以各调各的，
    /// 因为频段映射与包络都在组件侧，分析层只提供共享的原始谱。
    /// </summary>
    public class SpectrumComponentConfig : ObservableRecipient
    {
        // 默认律动条而非频谱柱：岛内槽位通常只有几十像素高，
        // 16 段频谱在该尺寸下视觉是糊的，单条律动条任何尺寸都读得清。
        private SpectrumVisualMode _mode = SpectrumVisualMode.Pulse;
        private int _bandCount = 16;
        private bool _isMirrored;
        private double _minFrequencyHz = 80;
        private double _maxFrequencyHz = 2000;
        private double _decayPerSecond = 2.5;
        private double _width = 80;
        private double _barGap = 2;
        private double _barCornerRadius = 1;
        private bool _useAccentColor = true;

        public SpectrumVisualMode Mode
        {
            get => _mode;
            set
            {
                if (_mode == value) return;
                _mode = value;
                OnPropertyChanged();
            }
        }

        public const int MinBandCount = 1;
        public const int MaxBandCount = 64;

        // 下面每对区间旁都跟一份 decimal 镜像，供设置页的 NumericUpDown 用。
        // 那两个属性是 decimal?，而 {x:Static} 不做 XAML 的字面量类型转换——
        // 直接引用 int 或 double 的静态量在 AXAML 编译期就被拒（AVLN3000）。
        // 镜像一律由上面那一份推导，不重新写数字：区间仍只有一个真值源，
        // 而 AXAML 引用错了名字是编译错误，不是静默回落到控件默认值。
        // 反过来把区间本身定成 decimal 不行：BandCount 是 int 域，
        // decimal 到 int 是截断转换，会让「夹紧值」与「界面显示值」有分裂的余地。
        public static readonly decimal BandCountMinimum = MinBandCount;
        public static readonly decimal BandCountMaximum = MaxBandCount;

        public int BandCount
        {
            get => _bandCount;
            set
            {
                if (_bandCount == value) return;
                _bandCount = Math.Clamp(value, MinBandCount, MaxBandCount);
                OnPropertyChanged();
            }
        }

        public bool IsMirrored
        {
            get => _isMirrored;
            set
            {
                if (_isMirrored == value) return;
                _isMirrored = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// 频率下限的取值范围。上界不到 20000：下限若能顶到听觉上限，
        /// 可选区间会退化成空的，界面上表现为频谱整片消失。
        /// </summary>
        public const double MinFrequencyLowerHz = 20;

        public const double MaxFrequencyLowerHz = 19000;

        public static readonly decimal FrequencyLowerMinimum = (decimal)MinFrequencyLowerHz;
        public static readonly decimal FrequencyLowerMaximum = (decimal)MaxFrequencyLowerHz;

        public double MinFrequencyHz
        {
            get => _minFrequencyHz;
            set
            {
                if (Math.Abs(_minFrequencyHz - value) < 0.01) return;
                _minFrequencyHz = Math.Clamp(value, MinFrequencyLowerHz, MaxFrequencyLowerHz);
                OnPropertyChanged();
            }
        }

        public const double MinFrequencyUpperHz = 100;
        public const double MaxFrequencyUpperHz = 20000;

        public static readonly decimal FrequencyUpperMinimum = (decimal)MinFrequencyUpperHz;
        public static readonly decimal FrequencyUpperMaximum = (decimal)MaxFrequencyUpperHz;

        public double MaxFrequencyHz
        {
            get => _maxFrequencyHz;
            set
            {
                if (Math.Abs(_maxFrequencyHz - value) < 0.01) return;
                _maxFrequencyHz = Math.Clamp(value, MinFrequencyUpperHz, MaxFrequencyUpperHz);
                OnPropertyChanged();
            }
        }

        /// <summary>下界不取零：零意味着永不回落，包络会卡在历史峰值上。</summary>
        public const double MinDecayPerSecond = 0.2;

        public const double MaxDecayPerSecond = 20;

        public static readonly decimal DecayMinimum = (decimal)MinDecayPerSecond;
        public static readonly decimal DecayMaximum = (decimal)MaxDecayPerSecond;

        /// <summary>每秒衰减比例。值越大回落越快，视觉越「跳」。</summary>
        public double DecayPerSecond
        {
            get => _decayPerSecond;
            set
            {
                if (Math.Abs(_decayPerSecond - value) < 0.01) return;
                _decayPerSecond = Math.Clamp(value, MinDecayPerSecond, MaxDecayPerSecond);
                OnPropertyChanged();
            }
        }

        // 不叫 MinWidth / MaxWidth：那是 Avalonia 控件上真实存在的属性名，
        // 在本类上虽不冲突，读代码的人却会误认成控件的那一对。
        public const double MinComponentWidth = 16;
        public const double MaxComponentWidth = 480;

        public static readonly decimal ComponentWidthMinimum = (decimal)MinComponentWidth;
        public static readonly decimal ComponentWidthMaximum = (decimal)MaxComponentWidth;

        public double Width
        {
            get => _width;
            set
            {
                if (Math.Abs(_width - value) < 0.01) return;
                _width = Math.Clamp(value, MinComponentWidth, MaxComponentWidth);
                OnPropertyChanged();
            }
        }

        public const double MinBarGap = 0;
        public const double MaxBarGap = 16;

        public static readonly decimal BarGapMinimum = (decimal)MinBarGap;
        public static readonly decimal BarGapMaximum = (decimal)MaxBarGap;

        public double BarGap
        {
            get => _barGap;
            set
            {
                if (Math.Abs(_barGap - value) < 0.01) return;
                _barGap = Math.Clamp(value, MinBarGap, MaxBarGap);
                OnPropertyChanged();
            }
        }

        public const double MinBarCornerRadius = 0;
        public const double MaxBarCornerRadius = 16;

        public static readonly decimal BarCornerRadiusMinimum = (decimal)MinBarCornerRadius;
        public static readonly decimal BarCornerRadiusMaximum = (decimal)MaxBarCornerRadius;

        public double BarCornerRadius
        {
            get => _barCornerRadius;
            set
            {
                if (Math.Abs(_barCornerRadius - value) < 0.01) return;
                _barCornerRadius = Math.Clamp(value, MinBarCornerRadius, MaxBarCornerRadius);
                OnPropertyChanged();
            }
        }

        /// <summary>用主题强调色而非继承的前景色。与进度条同源，让频谱和岛上其余元素成套。</summary>
        public bool UseAccentColor
        {
            get => _useAccentColor;
            set
            {
                if (_useAccentColor == value) return;
                _useAccentColor = value;
                OnPropertyChanged();
            }
        }
    }
}
