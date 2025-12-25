using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MediaIsland.Models
{
    public class PluginSettings : ObservableRecipient
    {
        // Not implemented
        private bool _isLyricGetterEnabled = false;

        public bool IsLyricGetterEnabled
        {
            get => _isLyricGetterEnabled;
            set
            {
                if (_isLyricGetterEnabled == value) return;
                _isLyricGetterEnabled = value;
                OnPropertyChanged();
            }
        }
        private bool _isCutSpotifyTrademarkEnabled = false;

        public bool IsCutSpotifyTrademarkEnabled
        {
            get => _isCutSpotifyTrademarkEnabled;
            set
            {
                if (_isCutSpotifyTrademarkEnabled  == value) return;
                _isCutSpotifyTrademarkEnabled = value;
                OnPropertyChanged();
            }
        }

        public ObservableCollection<MediaSource> MediaSourceList { get; set; } = [];
        
        private bool _isTodayEatSentry = true;

        public bool IsTodayEatSentry
        {
            get => _isTodayEatSentry;
            set
            {
                if (_isTodayEatSentry == value) return;
                _isTodayEatSentry = value;
                OnPropertyChanged();
            }
        }

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
