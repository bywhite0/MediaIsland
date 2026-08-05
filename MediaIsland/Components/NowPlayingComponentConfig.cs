using CommunityToolkit.Mvvm.ComponentModel;

namespace MediaIsland.Components
{
    public class NowPlayingComponentConfig : ObservableRecipient
    {

        bool _isHideWhenPaused = false;
        bool _isShowSource = false;
        bool _isShowSourceName = true;
        double _sourceIconRadius = 16.0;
        bool _isShowAlbumArt = true;
        bool _isShowPlaybackStatus = false;
        bool _isShowProgressBar = false;
        bool _isProgressBarLeftMargin;
        bool _isProgressBarRightMargin;
        bool _isLeftNegativeMargin = false;
        bool _isRightNegativeMargin = false;
        double _maxContentWidth = 0;
        bool _isScrollWhenOverflow = false;
        int _subInfoType = 0;

        /// <Summary>
        /// 0: 艺术家<br/>
        /// 1: 时间轴(如果可用)<br/>
        ///</Summary>
        public int SubInfoType
        {
            get => _subInfoType;
            set
            {
                if (_subInfoType == value) return;
                _subInfoType = value;
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
        public bool IsShowSource
        {
            get => _isShowSource;
            set
            {
                if (_isShowSource == value) return;
                _isShowSource = value;
                OnPropertyChanged();
            }
        }
        public bool IsShowSourceName
        {
            get => _isShowSourceName;
            set
            {
                if (_isShowSourceName == value) return;
                _isShowSourceName = value;
                OnPropertyChanged();
            }
        }

        public double SourceIconRadius
        {
            get => _sourceIconRadius;
            set
            {
                if (value.Equals(_sourceIconRadius)) return;
                _sourceIconRadius = value;
                OnPropertyChanged();
            }
        }
        public bool IsShowAlbumArt
        {
            get => _isShowAlbumArt;
            set
            {
                if (_isShowAlbumArt == value) return;
                _isShowAlbumArt = value;
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

        /// <summary>
        /// 是否在组件底部显示播放进度条。
        /// </summary>
        public bool IsShowProgressBar
        {
            get => _isShowProgressBar;
            set
            {
                if (_isShowProgressBar == value) return;
                _isShowProgressBar = value;
                OnPropertyChanged();
            }
        }


        /// <summary>
        /// 是否为进度条增加左边距，避免贴边时被岛屿圆角裁切。
        /// </summary>
        public bool IsProgressBarLeftMargin
        {
            get => _isProgressBarLeftMargin;
            set
            {
                if (_isProgressBarLeftMargin == value) return;
                _isProgressBarLeftMargin = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// 是否为进度条增加右边距，避免贴边时被岛屿圆角裁切。
        /// </summary>
        public bool IsProgressBarRightMargin
        {
            get => _isProgressBarRightMargin;
            set
            {
                if (_isProgressBarRightMargin == value) return;
                _isProgressBarRightMargin = value;
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
