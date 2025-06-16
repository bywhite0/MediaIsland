using CommunityToolkit.Mvvm.ComponentModel;

namespace MediaIsland.Components
{
    public class SimplyNowPlayingComponentConfig : ObservableRecipient
    {

        bool _isHideWhenPaused = false;
        bool _isShowPlaybackStatus = false;
        int _infoType = 1;

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
    }
}
