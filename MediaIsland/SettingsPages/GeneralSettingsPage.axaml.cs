using System.IO;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Controls;
using ClassIsland.Core.Enums.SettingsWindow;
using ClassIsland.Shared.Helpers;
using MediaIsland.Helpers;
using MediaIsland.Models;
using MediaIsland.Services.Media;
using MediaIsland.Services.Media.SourceDisplay;

namespace MediaIsland.SettingsPages
{
    /// <summary>
    /// GeneralSettingsPage.xaml 的交互逻辑
    /// </summary>
    [SettingsPageInfo(
        "mediaisland.general",
        "MediaIsland",
        "\uEBCA",
        "\uEBC9",
        SettingsPageCategory.External)]
    public partial class GeneralSettingsPage : SettingsPageBase, INotifyPropertyChanged
    {
        public Plugin Plugin { get; }
        public PluginSettings Settings { get; }
        private readonly IMediaService _mediaService;
        private readonly IMediaSourceDisplayService _mediaSourceDisplayService;
        private bool _isDetached;
        private string _currentMediaTitle = "未检测到正在播放的媒体";
        private string _currentMediaArtistAlbum = "播放媒体后会在此处显示标题、艺术家、专辑与进度。";
        private string _currentMediaPlaybackStatus = "无媒体";
        private string _currentMediaStatusGlyph = "\uE9CE";
        private string _currentMediaTimeline = "00:00 / 00:00";
        private string _currentMediaSourceDisplay = "播放源：-";
        private string _currentMediaSourceId = "播放源 ID：-";
        private string _currentMediaSourceIconStatus = "图标状态：无图标";
        private Bitmap? _currentMediaThumbnail;
        private Bitmap? _currentMediaSourceIcon;

        private event PropertyChangedEventHandler? NotifyPropertyChanged;

        event PropertyChangedEventHandler? INotifyPropertyChanged.PropertyChanged
        {
            add => NotifyPropertyChanged += value;
            remove => NotifyPropertyChanged -= value;
        }

        public string CurrentMediaTitle
        {
            get => _currentMediaTitle;
            private set => SetProperty(ref _currentMediaTitle, value);
        }

        public string CurrentMediaArtistAlbum
        {
            get => _currentMediaArtistAlbum;
            private set => SetProperty(ref _currentMediaArtistAlbum, value);
        }

        public string CurrentMediaPlaybackStatus
        {
            get => _currentMediaPlaybackStatus;
            private set => SetProperty(ref _currentMediaPlaybackStatus, value);
        }

        public string CurrentMediaStatusGlyph
        {
            get => _currentMediaStatusGlyph;
            private set => SetProperty(ref _currentMediaStatusGlyph, value);
        }

        public string CurrentMediaTimeline
        {
            get => _currentMediaTimeline;
            private set => SetProperty(ref _currentMediaTimeline, value);
        }

        public string CurrentMediaSourceDisplay
        {
            get => _currentMediaSourceDisplay;
            private set => SetProperty(ref _currentMediaSourceDisplay, value);
        }

        public string CurrentMediaSourceId
        {
            get => _currentMediaSourceId;
            private set => SetProperty(ref _currentMediaSourceId, value);
        }

        public string CurrentMediaSourceIconStatus
        {
            get => _currentMediaSourceIconStatus;
            private set => SetProperty(ref _currentMediaSourceIconStatus, value);
        }

        public Bitmap? CurrentMediaThumbnail
        {
            get => _currentMediaThumbnail;
            private set
            {
                if (!SetProperty(ref _currentMediaThumbnail, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(HasCurrentMediaThumbnail));
            }
        }

        public bool HasCurrentMediaThumbnail => CurrentMediaThumbnail != null;

        public Bitmap? CurrentMediaSourceIcon
        {
            get => _currentMediaSourceIcon;
            private set
            {
                if (!SetProperty(ref _currentMediaSourceIcon, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(HasCurrentMediaSourceIcon));
            }
        }

        public bool HasCurrentMediaSourceIcon => CurrentMediaSourceIcon != null;

        public GeneralSettingsPage(
            Plugin plugin,
            IMediaService mediaService,
            IMediaSourceDisplayService mediaSourceDisplayService)
        {
            Plugin = plugin;
            Settings = Plugin.Settings;
            _mediaService = mediaService;
            _mediaSourceDisplayService = mediaSourceDisplayService;
            RemoveNullMediaSources();
            InitializeComponent();
            DetachedFromVisualTree += (_, _) =>
            {
                _isDetached = true;
                _mediaService.MediaInfoChanged -= MediaService_OnMediaInfoChanged;
            };
            Settings.needRestart += RequestRestart;
            _mediaService.MediaInfoChanged += MediaService_OnMediaInfoChanged;
            StartMediaServiceAsync();
            AddCurrentMediaSourceIfAvailable();
            _ = RefreshCurrentMediaInfoAsync(_mediaService.CurrentMediaInfo);
            RefreshMediaSourceDisplayInfos();
            var screenshotApp = new MediaSource
            {
                Source = "Microsoft.ScreenSketch_8wekyb3d8bbwe!App",
                IsEnabled = false
            };
            if (!Settings.MediaSourceList.Any(source => source?.Source == "Microsoft.ScreenSketch_8wekyb3d8bbwe!App"))
            {
                Settings.MediaSourceList.Add(screenshotApp);
                SaveSettings();
            }
        }

        private async void StartMediaServiceAsync()
        {
            try
            {
                await _mediaService.EnsureStartedAsync();
                AddCurrentMediaSourceIfAvailable();
                await RefreshCurrentMediaInfoAsync(_mediaService.CurrentMediaInfo);
            }
            catch
            {
                // Media source discovery is best-effort on the settings page.
            }
        }

        private void MediaService_OnMediaInfoChanged(object? sender, MediaInfoChangedEventArgs e)
        {
            _ = RefreshCurrentMediaInfoAsync(e.MediaInfo);
            if (e.MediaInfo == null)
            {
                return;
            }

            Dispatcher.UIThread.Post(() => AddMediaSource(e.MediaInfo.SourceApp));
        }

        static bool IsLyricsIslandInstalled()
        {
            return IntegrationHelper.IsPluginInstalled("jiangyin14.lyrics");
        }

        static bool IsExtraIslandInstalled()
        {
            return IntegrationHelper.IsPluginInstalled("ink.lipoly.ext.extraisland");
        }
        public static bool IsLyricsIslandExisted()
        {
            return IsLyricsIslandInstalled() || IsExtraIslandInstalled();
        }

        private void AddButtonOnClick(object sender,RoutedEventArgs e)
        {
            if (_mediaService.CurrentMediaInfo == null)
            {
                CommonTaskDialogs.ShowDialog("添加媒体源时发生错误", "未检测到正在播放的媒体，请播放媒体后再试。");
                return;
            }

            var currentSource = _mediaService.CurrentMediaInfo.SourceApp;
            if (AddMediaSource(currentSource))
            {
                return;
            }

            CommonTaskDialogs.ShowDialog("添加媒体源时发生错误", "列表已存在该媒体源。");
        }

        private void AddCurrentMediaSourceIfAvailable()
        {
            if (_mediaService.CurrentMediaInfo == null)
            {
                return;
            }

            AddMediaSource(_mediaService.CurrentMediaInfo.SourceApp);
        }

        private bool AddMediaSource(string currentSource)
        {
            var sourceItem = new MediaSource
            {
                Source = currentSource
            };
            if (Settings.MediaSourceList.All(source => source?.Source != currentSource))
            {
                Settings.MediaSourceList.Add(sourceItem);
                _ = RefreshMediaSourceDisplayInfoAsync(sourceItem);
                SaveSettings();
                return true;
            }

            return false;
        }

        private void RemoveNullMediaSources()
        {
            var removed = false;
            for (var index = Settings.MediaSourceList.Count - 1; index >= 0; index--)
            {
                if (Settings.MediaSourceList[index] != null)
                {
                    continue;
                }

                Settings.MediaSourceList.RemoveAt(index);
                removed = true;
            }

            if (removed)
            {
                SaveSettings();
            }
        }

        private void DeleteButtonOnClick(object sender,RoutedEventArgs e)
        {
            Button button = (sender as Button)!;
            if (button.DataContext is MediaSource item)
            {
                Settings.MediaSourceList.Remove(item);
                _mediaSourceDisplayService.Invalidate(item.Source);
                SaveSettings();
            }
        }

        private async void ChooseIconButtonOnClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: MediaSource item })
            {
                return;
            }

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null)
            {
                return;
            }

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择播放源图标",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("图片")
                    {
                        Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp"]
                    }
                ]
            });

            var iconPath = files.FirstOrDefault()?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(iconPath))
            {
                return;
            }

            item.IconPath = iconPath;
            _mediaSourceDisplayService.Invalidate(item.Source);
            await RefreshMediaSourceDisplayInfoAsync(item);
            SaveSettings();
        }

        private async void ClearIconButtonOnClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: MediaSource item })
            {
                return;
            }

            item.IconPath = null;
            _mediaSourceDisplayService.Invalidate(item.Source);
            await RefreshMediaSourceDisplayInfoAsync(item);
            SaveSettings();
        }

        private async void RefreshMediaSourceDisplayInfos()
        {
            foreach (var source in Settings.MediaSourceList.Where(source => source != null).ToArray())
            {
                await RefreshMediaSourceDisplayInfoAsync(source);
            }
        }

        private async Task RefreshMediaSourceDisplayInfoAsync(MediaSource item)
        {
            if (string.IsNullOrWhiteSpace(item.Source))
            {
                item.DisplayName = string.Empty;
                item.DisplayIcon = null;
                item.IconStatus = "未设置";
                return;
            }

            item.IconStatus = "解析中";
            try
            {
                var displayInfo = await _mediaSourceDisplayService.ResolveAsync(item.Source);
                item.DisplayName = displayInfo.DisplayName;
                item.DisplayIcon = displayInfo.Icon;
                item.IconStatus = GetIconStatus(displayInfo.Kind, displayInfo.Icon != null);
            }
            catch
            {
                item.DisplayName = item.Source;
                item.DisplayIcon = null;
                item.IconStatus = "不可用";
            }
        }

        private static string GetIconStatus(MediaSourceDisplayKind kind, bool hasIcon)
        {
            return kind switch
            {
                MediaSourceDisplayKind.UserConfigured when hasIcon => "自定义",
                MediaSourceDisplayKind.Platform when hasIcon => "系统",
                MediaSourceDisplayKind.Bundled when hasIcon => "内置",
                MediaSourceDisplayKind.Mapping => "名称映射",
                MediaSourceDisplayKind.Unknown => "无图标",
                _ => hasIcon ? "已解析" : "无图标"
            };
        }

        private async Task RefreshCurrentMediaInfoAsync(MediaInfo? info)
        {
            if (info == null)
            {
                await UpdateCurrentMediaUiAsync(ClearCurrentMediaInfo);
                return;
            }

            try
            {
                var sourceInfo = await _mediaSourceDisplayService.ResolveAsync(info.SourceApp);
                await UpdateCurrentMediaUiAsync(() =>
                {
                    CurrentMediaTitle = string.IsNullOrWhiteSpace(info.Title) ? "未知标题" : info.Title;
                    CurrentMediaArtistAlbum = FormatArtistAlbum(info.Artist, info.AlbumTitle);
                    CurrentMediaPlaybackStatus = GetPlaybackStatusText(info.PlaybackInfo.PlaybackState);
                    CurrentMediaStatusGlyph = GetPlaybackStatusGlyph(info.PlaybackInfo.PlaybackState);
                    CurrentMediaTimeline = FormatTimeline(info.Position, info.Duration);
                    CurrentMediaSourceDisplay = $"播放源：{sourceInfo.DisplayName}";
                    CurrentMediaSourceId = $"播放源 ID：{info.SourceApp}";
                    CurrentMediaSourceIcon = sourceInfo.Icon;
                    CurrentMediaSourceIconStatus = $"图标状态：{GetIconStatus(sourceInfo.Kind, sourceInfo.Icon != null)}";
                    CurrentMediaThumbnail = info.Thumbnail;
                });
            }
            catch
            {
                await UpdateCurrentMediaUiAsync(() =>
                {
                    CurrentMediaTitle = string.IsNullOrWhiteSpace(info.Title) ? "未知标题" : info.Title;
                    CurrentMediaArtistAlbum = FormatArtistAlbum(info.Artist, info.AlbumTitle);
                    CurrentMediaPlaybackStatus = GetPlaybackStatusText(info.PlaybackInfo.PlaybackState);
                    CurrentMediaStatusGlyph = GetPlaybackStatusGlyph(info.PlaybackInfo.PlaybackState);
                    CurrentMediaTimeline = FormatTimeline(info.Position, info.Duration);
                    CurrentMediaSourceDisplay = $"播放源：{info.SourceApp}";
                    CurrentMediaSourceId = $"播放源 ID：{info.SourceApp}";
                    CurrentMediaSourceIcon = null;
                    CurrentMediaSourceIconStatus = "图标状态：不可用";
                    CurrentMediaThumbnail = info.Thumbnail;
                });
            }
        }

        private void ClearCurrentMediaInfo()
        {
            CurrentMediaTitle = "未检测到正在播放的媒体";
            CurrentMediaArtistAlbum = "播放媒体后会在此处显示标题、艺术家、专辑与进度。";
            CurrentMediaPlaybackStatus = "无媒体";
            CurrentMediaStatusGlyph = "\uE9CE";
            CurrentMediaTimeline = "00:00 / 00:00";
            CurrentMediaSourceDisplay = "播放源：-";
            CurrentMediaSourceId = "播放源 ID：-";
            CurrentMediaSourceIcon = null;
            CurrentMediaSourceIconStatus = "图标状态：无图标";
            CurrentMediaThumbnail = null;
        }

        private async Task UpdateCurrentMediaUiAsync(Action update)
        {
            if (_isDetached)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_isDetached)
                {
                    return;
                }

                update();
            });
        }

        private static string FormatArtistAlbum(string? artist, string? album)
        {
            var displayArtist = string.IsNullOrWhiteSpace(artist) ? "未知艺术家" : artist;
            var displayAlbum = string.IsNullOrWhiteSpace(album) ? "未知专辑" : album;
            return $"{displayArtist} - {displayAlbum}";
        }

        private static string FormatTimeline(TimeSpan position, TimeSpan duration)
        {
            return duration.Hours == 0
                ? $@"{position:mm\:ss} / {duration:mm\:ss}"
                : $@"{position} / {duration}";
        }

        private static string GetPlaybackStatusText(MediaPlaybackState state)
        {
            return state switch
            {
                MediaPlaybackState.Playing => "正在播放",
                MediaPlaybackState.Paused => "已暂停",
                MediaPlaybackState.Stopped => "已停止",
                MediaPlaybackState.Changing => "切换中",
                MediaPlaybackState.Opened => "已打开",
                MediaPlaybackState.Closed => "已关闭",
                _ => "未知状态"
            };
        }

        private static string GetPlaybackStatusGlyph(MediaPlaybackState state)
        {
            return state switch
            {
                MediaPlaybackState.Playing => "\uEDB8",
                MediaPlaybackState.Paused => "\uEC90",
                MediaPlaybackState.Stopped => "\uF086",
                MediaPlaybackState.Changing => "\uE0B4",
                _ => "\uE9CE"
            };
        }

        private void SaveSettings()
        {
            ConfigureFileHelper.SaveConfig<PluginSettings>(Path.Combine(Plugin.globalConfigFolder!, "Settings.json"), Settings);
        }

        private void SaveButtonOnClick(object sender, RoutedEventArgs e)
        {
            SaveSettings();
        }

        private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            NotifyPropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
