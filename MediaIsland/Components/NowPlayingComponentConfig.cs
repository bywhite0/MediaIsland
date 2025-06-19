using CommunityToolkit.Mvvm.ComponentModel;

namespace MediaIsland.Components
{
    public class NowPlayingComponentConfig : ObservableRecipient
    {

        bool _isHideWhenPaused = false;
        bool _isShowSource = true;
        bool _isShowSourceName = true;
        bool _isShowAlbumArt = true;
        bool _isShowPlaybackStatus = false;
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
    }
}
