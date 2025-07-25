using System.Collections.ObjectModel;
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

        public ObservableCollection<MediaSource> MediaSourceList { get; set; } = [];
        
    }
    public class MediaSource : ObservableObject
    {
        private string _source = string.Empty;
        private bool _isEnabled = true;

        public string Source
        {
            get => _source;
            set
            {
                if (_source == value) return;
                _source = value;
                OnPropertyChanged();
            }
        }

        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled == value) return;
                _isEnabled = value;
                OnPropertyChanged();
            }
        }
    }
}
