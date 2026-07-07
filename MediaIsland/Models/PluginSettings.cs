using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using ClassIsland.Core.Abstractions;

namespace MediaIsland.Models
{
    public class PluginSettings : ObservableRecipient
    {
        public Action? needRestart;
        
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
                needRestart?.Invoke();
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
        private string? _iconPath;
        private string _displayName = string.Empty;
        private string _iconStatus = "未解析";
        private Bitmap? _displayIcon;

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

        public string? IconPath
        {
            get => _iconPath;
            set
            {
                if (_iconPath == value) return;
                _iconPath = value;
                OnPropertyChanged();
            }
        }

        [JsonIgnore]
        public string DisplayName
        {
            get => string.IsNullOrWhiteSpace(_displayName) ? Source : _displayName;
            set
            {
                if (_displayName == value) return;
                _displayName = value;
                OnPropertyChanged();
            }
        }

        [JsonIgnore]
        public string IconStatus
        {
            get => _iconStatus;
            set
            {
                if (_iconStatus == value) return;
                _iconStatus = value;
                OnPropertyChanged();
            }
        }

        [JsonIgnore]
        public Bitmap? DisplayIcon
        {
            get => _displayIcon;
            set
            {
                if (ReferenceEquals(_displayIcon, value)) return;
                _displayIcon = value;
                OnPropertyChanged();
            }
        }
    }
}
