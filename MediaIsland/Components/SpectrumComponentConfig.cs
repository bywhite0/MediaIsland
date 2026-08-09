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

        public int BandCount
        {
            get => _bandCount;
            set
            {
                if (_bandCount == value) return;
                _bandCount = Math.Clamp(value, 1, 64);
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

        public double MinFrequencyHz
        {
            get => _minFrequencyHz;
            set
            {
                if (Math.Abs(_minFrequencyHz - value) < 0.01) return;
                _minFrequencyHz = Math.Clamp(value, 20, 19000);
                OnPropertyChanged();
            }
        }

        public double MaxFrequencyHz
        {
            get => _maxFrequencyHz;
            set
            {
                if (Math.Abs(_maxFrequencyHz - value) < 0.01) return;
                _maxFrequencyHz = Math.Clamp(value, 100, 20000);
                OnPropertyChanged();
            }
        }

        /// <summary>每秒衰减比例。值越大回落越快，视觉越「跳」。</summary>
        public double DecayPerSecond
        {
            get => _decayPerSecond;
            set
            {
                if (Math.Abs(_decayPerSecond - value) < 0.01) return;
                _decayPerSecond = Math.Clamp(value, 0.2, 20);
                OnPropertyChanged();
            }
        }

        public double Width
        {
            get => _width;
            set
            {
                if (Math.Abs(_width - value) < 0.01) return;
                _width = Math.Clamp(value, 16, 480);
                OnPropertyChanged();
            }
        }

        public double BarGap
        {
            get => _barGap;
            set
            {
                if (Math.Abs(_barGap - value) < 0.01) return;
                _barGap = Math.Clamp(value, 0, 16);
                OnPropertyChanged();
            }
        }

        public double BarCornerRadius
        {
            get => _barCornerRadius;
            set
            {
                if (Math.Abs(_barCornerRadius - value) < 0.01) return;
                _barCornerRadius = Math.Clamp(value, 0, 16);
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
