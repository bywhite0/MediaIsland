using CommunityToolkit.Mvvm.ComponentModel;

namespace MediaIsland.Components
{
    public class NowPlayingComponentConfig : ObservableRecipient
    {

        bool _isHideWhenPaused = false;
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
    }
}
