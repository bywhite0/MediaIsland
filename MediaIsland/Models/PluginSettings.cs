using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using ClassIsland.Core.Abstractions;
using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Lyrics.Models;
using MediaIsland.Services.MediaLink;

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

        private bool _isLyricsTransitionEnabled = true;

        public bool IsLyricsTransitionEnabled
        {
            get => _isLyricsTransitionEnabled;
            set
            {
                if (_isLyricsTransitionEnabled == value) return;
                _isLyricsTransitionEnabled = value;
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

        private bool _isWordLyricsEmphasisGlowEnabled = true;

        public bool IsWordLyricsEmphasisGlowEnabled
        {
            get => _isWordLyricsEmphasisGlowEnabled;
            set
            {
                if (_isWordLyricsEmphasisGlowEnabled == value) return;
                _isWordLyricsEmphasisGlowEnabled = value;
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

        private string _lyricsOriginalFontFamily = string.Empty;
        private string _lyricsTranslationFontFamily = string.Empty;
        private string _lyricsRomanizationFontFamily = string.Empty;
        private int _lyricsOriginalFontWeight;
        private int _lyricsTranslationFontWeight;
        private int _lyricsRomanizationFontWeight;

        /// <summary>
        /// 原文歌词字体，留空表示跟随 ClassIsland 全局字体。
        /// </summary>
        public string LyricsOriginalFontFamily
        {
            get => _lyricsOriginalFontFamily;
            set => SetLyricsFontFamily(ref _lyricsOriginalFontFamily, value);
        }

        /// <summary>
        /// 翻译歌词字体，留空表示跟随 ClassIsland 全局字体。
        /// </summary>
        public string LyricsTranslationFontFamily
        {
            get => _lyricsTranslationFontFamily;
            set => SetLyricsFontFamily(ref _lyricsTranslationFontFamily, value);
        }

        /// <summary>
        /// 音译歌词字体，留空表示跟随 ClassIsland 全局字体。
        /// </summary>
        public string LyricsRomanizationFontFamily
        {
            get => _lyricsRomanizationFontFamily;
            set => SetLyricsFontFamily(ref _lyricsRomanizationFontFamily, value);
        }

        /// <summary>
        /// 原文歌词字重，0 表示跟随全局。
        /// </summary>
        public int LyricsOriginalFontWeight
        {
            get => _lyricsOriginalFontWeight;
            set => SetLyricsFontWeight(
                ref _lyricsOriginalFontWeight,
                value,
                nameof(LyricsOriginalFontWeightIndex));
        }

        /// <summary>
        /// 翻译歌词字重，0 表示跟随全局。
        /// </summary>
        public int LyricsTranslationFontWeight
        {
            get => _lyricsTranslationFontWeight;
            set => SetLyricsFontWeight(
                ref _lyricsTranslationFontWeight,
                value,
                nameof(LyricsTranslationFontWeightIndex));
        }

        /// <summary>
        /// 音译歌词字重，0 表示跟随全局。
        /// </summary>
        public int LyricsRomanizationFontWeight
        {
            get => _lyricsRomanizationFontWeight;
            set => SetLyricsFontWeight(
                ref _lyricsRomanizationFontWeight,
                value,
                nameof(LyricsRomanizationFontWeightIndex));
        }

        [JsonIgnore]
        public int LyricsOriginalFontWeightIndex
        {
            get => LyricsTypography.ToFontWeightIndex(LyricsOriginalFontWeight);
            set => LyricsOriginalFontWeight = LyricsTypography.FromFontWeightIndex(value);
        }

        [JsonIgnore]
        public int LyricsTranslationFontWeightIndex
        {
            get => LyricsTypography.ToFontWeightIndex(LyricsTranslationFontWeight);
            set => LyricsTranslationFontWeight = LyricsTypography.FromFontWeightIndex(value);
        }

        [JsonIgnore]
        public int LyricsRomanizationFontWeightIndex
        {
            get => LyricsTypography.ToFontWeightIndex(LyricsRomanizationFontWeight);
            set => LyricsRomanizationFontWeight = LyricsTypography.FromFontWeightIndex(value);
        }

        private void SetLyricsFontFamily(
            ref string field,
            string? value,
            [CallerMemberName] string? propertyName = null)
        {
            var normalizedValue = LyricsTypography.NormalizeFontFamily(value);
            if (field == normalizedValue) return;
            field = normalizedValue;
            OnPropertyChanged(propertyName);
        }

        private void SetLyricsFontWeight(
            ref int field,
            int value,
            string indexPropertyName,
            [CallerMemberName] string? propertyName = null)
        {
            var normalizedValue = LyricsTypography.NormalizeFontWeight(value);
            if (field == normalizedValue) return;
            field = normalizedValue;
            OnPropertyChanged(propertyName);
            OnPropertyChanged(indexPropertyName);
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

        private int _progressBarColorMode = 0; // ProgressBarColorMode.ClassIslandTheme

        /// <summary>
        /// 正在播放组件进度条颜色来源。
        /// 0: ClassIsland 主题色；1: 封面主题色（如有，否则回退主题色）。
        /// </summary>
        public int ProgressBarColorMode
        {
            get => _progressBarColorMode;
            set
            {
                if (_progressBarColorMode == value) return;
                _progressBarColorMode = value;
                OnPropertyChanged();
            }
        }

        private bool _mediaLinkIsEnabled;
        private string _mediaLinkListenAddress = "0.0.0.0";
        private int _mediaLinkPort = 17654;
        private string _mediaLinkToken = string.Empty;
        private int _mediaLinkTimelineMinIntervalMs = 200;

        public bool MediaLinkIsEnabled
        {
            get => _mediaLinkIsEnabled;
            set
            {
                if (_mediaLinkIsEnabled == value) return;
                _mediaLinkIsEnabled = value;
                OnPropertyChanged();
            }
        }

        public string MediaLinkListenAddress
        {
            get => _mediaLinkListenAddress;
            set
            {
                var normalized = string.IsNullOrWhiteSpace(value) ? "0.0.0.0" : value.Trim();
                if (_mediaLinkListenAddress == normalized) return;
                _mediaLinkListenAddress = normalized;
                OnPropertyChanged();
            }
        }

        public int MediaLinkPort
        {
            get => _mediaLinkPort;
            set
            {
                var normalized = value is < 1 or > 65535 ? 17654 : value;
                if (_mediaLinkPort == normalized) return;
                _mediaLinkPort = normalized;
                OnPropertyChanged();
            }
        }

        public string MediaLinkToken
        {
            get => _mediaLinkToken;
            set
            {
                var normalized = value ?? string.Empty;
                if (_mediaLinkToken == normalized) return;
                _mediaLinkToken = normalized;
                OnPropertyChanged();
            }
        }

        public int MediaLinkTimelineMinIntervalMs
        {
            get => _mediaLinkTimelineMinIntervalMs;
            set
            {
                var normalized = value < 0 ? 200 : value;
                if (_mediaLinkTimelineMinIntervalMs == normalized) return;
                _mediaLinkTimelineMinIntervalMs = normalized;
                OnPropertyChanged();
            }
        }

        private MediaLinkMediaSourceMode _mediaLinkMediaSourceMode = MediaLinkMediaSourceMode.PlatformOnly;
        private bool _mediaLinkUiUsesEffective = true;
        private bool _mediaLinkPushUsesEffective = true;

        public MediaLinkMediaSourceMode MediaLinkMediaSourceMode
        {
            get => _mediaLinkMediaSourceMode;
            set
            {
                if (_mediaLinkMediaSourceMode == value) return;
                _mediaLinkMediaSourceMode = value;
                OnPropertyChanged();
            }
        }

        public bool MediaLinkUiUsesEffective
        {
            get => _mediaLinkUiUsesEffective;
            set
            {
                if (_mediaLinkUiUsesEffective == value) return;
                _mediaLinkUiUsesEffective = value;
                OnPropertyChanged();
            }
        }

        public bool MediaLinkPushUsesEffective
        {
            get => _mediaLinkPushUsesEffective;
            set
            {
                if (_mediaLinkPushUsesEffective == value) return;
                _mediaLinkPushUsesEffective = value;
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

