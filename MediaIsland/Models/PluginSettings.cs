using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using ClassIsland.Core.Abstractions;
using MediaIsland.Services.Lyrics.Models;

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

        public event EventHandler? MediaSourceSettingsSaved;

        public void NotifyMediaSourceSettingsSaved()
        {
            MediaSourceSettingsSaved?.Invoke(this, EventArgs.Empty);
        }

        private LyricsSourceSettings _lyrics = LyricsSourceSettings.Normalize(new LyricsSourceSettings());

        public LyricsSourceSettings Lyrics
        {
            get => _lyrics;
            set
            {
                _lyrics = LyricsSourceSettings.Normalize(value ?? new LyricsSourceSettings());
                OnPropertyChanged();
            }
        }

        [JsonIgnore]
        public bool HasConfiguredLyricSources => Lyrics.Sources.Any(source => source.IsEnabled);

        private bool _isWordLyricsEnabled = true;

        public bool IsWordLyricsEnabled
        {
            get => _isWordLyricsEnabled;
            set
            {
                if (_isWordLyricsEnabled == value) return;
                _isWordLyricsEnabled = value;
                OnPropertyChanged();
            }
        }

        private bool _isLyricsInterludeAnimationEnabled = true;

        public bool IsLyricsInterludeAnimationEnabled
        {
            get => _isLyricsInterludeAnimationEnabled;
            set
            {
                if (_isLyricsInterludeAnimationEnabled == value) return;
                _isLyricsInterludeAnimationEnabled = value;
                OnPropertyChanged();
            }
        }

        private bool _isWordLyricsLiftEnabled = true;

        public bool IsWordLyricsLiftEnabled
        {
            get => _isWordLyricsLiftEnabled;
            set
            {
                if (_isWordLyricsLiftEnabled == value) return;
                _isWordLyricsLiftEnabled = value;
                OnPropertyChanged();
            }
        }

        private bool _isWordLyricsEmphasisEnabled = true;

        public bool IsWordLyricsEmphasisEnabled
        {
            get => _isWordLyricsEmphasisEnabled;
            set
            {
                if (_isWordLyricsEmphasisEnabled == value) return;
                _isWordLyricsEmphasisEnabled = value;
                OnPropertyChanged();
            }
        }

        private bool _isWordLyricsEdgeFeatherEnabled = true;

        public bool IsWordLyricsEdgeFeatherEnabled
        {
            get => _isWordLyricsEdgeFeatherEnabled;
            set
            {
                if (_isWordLyricsEdgeFeatherEnabled == value) return;
                _isWordLyricsEdgeFeatherEnabled = value;
                OnPropertyChanged();
            }
        }

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
        private const string LyricsSearchDisabledByDefaultSource = "top.imsyy.splayer-next";
        private string _source = string.Empty;
        private bool _isEnabled = true;
        private bool? _isLyricsSearchEnabled;
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
                OnPropertyChanged(nameof(DisplayName));
                OnPropertyChanged(nameof(IsLyricsSearchEnabled));
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

        public bool IsLyricsSearchEnabled
        {
            get => _isLyricsSearchEnabled ?? IsLyricsSearchEnabledByDefault(Source);
            set
            {
                if (_isLyricsSearchEnabled == value) return;
                _isLyricsSearchEnabled = value;
                OnPropertyChanged();
            }
        }

        public static bool IsLyricsSearchEnabledByDefault(string sourceApp)
        {
            return !string.Equals(
                sourceApp,
                LyricsSearchDisabledByDefaultSource,
                StringComparison.OrdinalIgnoreCase);
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

        private string? _customDisplayName;

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? CustomDisplayName
        {
            get => _customDisplayName;
            set
            {
                var normalizedValue = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                if (_customDisplayName == normalizedValue) return;
                _customDisplayName = normalizedValue;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayName));
            }
        }

        [JsonIgnore]
        public string DisplayName
        {
            get => !string.IsNullOrWhiteSpace(CustomDisplayName)
                ? CustomDisplayName
                : string.IsNullOrWhiteSpace(_displayName)
                    ? Source
                    : _displayName;
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
