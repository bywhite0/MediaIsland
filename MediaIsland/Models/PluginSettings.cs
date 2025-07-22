using CommunityToolkit.Mvvm.ComponentModel;

namespace MediaIsland.Models
{
    public class PluginSettings : ObservableRecipient
    {
        // TODO: Remove after ExtraIsland's new version release
        private bool _IsLXMusicLyricForwarderEnabled = false;
        int _LXMusicAPIPort = 23330;

        public bool IsLXMusicLyricForwarderEnabled
        {
            get => _IsLXMusicLyricForwarderEnabled;
            set
            {
                if (_IsLXMusicLyricForwarderEnabled == value) return;
                _IsLXMusicLyricForwarderEnabled = value;
                OnPropertyChanged();
            }
        }
        public int LXMusicAPIPort
        {
            get => _LXMusicAPIPort;
            set
            {
                if (_LXMusicAPIPort == value) return;
                _LXMusicAPIPort = value;
                OnPropertyChanged();
            }
        }
    }
}
