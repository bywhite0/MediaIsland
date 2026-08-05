using CommunityToolkit.Mvvm.ComponentModel;

namespace MediaIsland.Components
{
    public class SimplyNowPlayingComponentConfig : ObservableRecipient
    {

        bool _isHideWhenPaused = false;
        bool _isShowPlaybackStatus = false;
        int _infoType = 1;
        bool _isDualLineStyle = false;
        bool _isLeftNegativeMargin = false;
        bool _isRightNegativeMargin = false;
        double _maxContentWidth = 0;
        bool _isScrollWhenOverflow = false;

        /// <Summary>
        /// 0: 艺术家 - 歌曲名<br/>
        /// 1: 歌曲名 - 艺术家<br/>
        /// 2: 歌曲名<br/>
        ///</Summary>
        public int InfoType
        {
            get => _infoType;
            set
            {
                if (_infoType == value) return;
                _infoType = value;
                OnPropertyChanged();
            }
        }
        public bool IsHideWhenPaused
        {
            get => _isHideWhenPaused;
            set
            {
                if (_isHideWhenPaused == value) return;
                _isHideWhenPaused = value;
                OnPropertyChanged();
                
            }
        }
        public bool IsShowPlaybackStatus
        {
            get => _isShowPlaybackStatus;
            set
            {                   
                if (_isShowPlaybackStatus == value) return;
                _isShowPlaybackStatus = value;
                OnPropertyChanged();
            }
        }

        public bool IsDualLineStyle
        {
            get => _isDualLineStyle;
            set
            {
                if (_isDualLineStyle == value) return;
                _isDualLineStyle = value;
                OnPropertyChanged();
            }
        }

        public bool IsLeftNegativeMargin
        {
            get => _isLeftNegativeMargin;
            set
            {
                if (_isLeftNegativeMargin == value) return;
                _isLeftNegativeMargin = value;
                OnPropertyChanged();
            }
        }

        public bool IsRightNegativeMargin
        {
            get => _isRightNegativeMargin;
            set
            {
                if (_isRightNegativeMargin == value) return;
                _isRightNegativeMargin = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// 媒体信息区的最大宽度（像素）。0 表示不限制，保持组件随内容自由伸展的旧行为。
        /// 超出该宽度时按 <see cref="IsScrollWhenOverflow"/> 决定截断或滚动。
        /// </summary>
        public double MaxContentWidth
        {
            get => _maxContentWidth;
            set
            {
                var normalizedValue = double.IsFinite(value) && value > 0 ? Math.Clamp(value, 40, 1000) : 0;
                if (Math.Abs(_maxContentWidth - normalizedValue) < 0.001) return;
                _maxContentWidth = normalizedValue;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// 内容超出 <see cref="MaxContentWidth"/> 时横向滚动展示，而不是截断为省略号。
        /// </summary>
        public bool IsScrollWhenOverflow
        {
            get => _isScrollWhenOverflow;
            set
            {
                if (_isScrollWhenOverflow == value) return;
                _isScrollWhenOverflow = value;
                OnPropertyChanged();
            }
        }
    }
}
