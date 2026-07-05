using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Windows.Media.Control;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Controls.CommonDialog;
using ClassIsland.Core.Enums.SettingsWindow;
using ClassIsland.Shared.Helpers;
using MaterialDesignThemes.Wpf;
using MediaIsland.Helpers;
using MediaIsland.Models;
using MediaIsland.Services;

namespace MediaIsland.SettingsPages
{
    /// <summary>
    /// GeneralSettingsPage.xaml 的交互逻辑
    /// </summary>
    [SettingsPageInfo(
        "mediaisland.general",
        "MediaIsland",
        PackIconKind.MusicBoxOutline,
        PackIconKind.MusicBox,
        SettingsPageCategory.External)]
    public partial class GeneralSettingsPage
    {
        public Plugin Plugin { get; }
        public PluginSettings Settings { get; }
        private readonly IMediaService _mediaService;
        private bool _isUnloaded;

        public GeneralSettingsPage(Plugin plugin, IMediaService mediaService)
        {
            Plugin = plugin;
            Settings = Plugin.Settings;
            _mediaService = mediaService;
            InitializeComponent();
            DataContext = this;
            _mediaService.OnMediaPropertiesChanged += MediaService_OnMediaPropertiesChanged;
            _mediaService.OnPlaybackStateChanged += MediaService_OnPlaybackStateChanged;
            _mediaService.OnTimelinePropertyChanged += MediaService_OnTimelinePropertyChanged;
            _mediaService.OnFocusedSessionChanged += MediaService_OnFocusedSessionChanged;
            _ = RefreshCurrentMediaInfoAsync(_mediaService.CurrentMediaInfo);
            var screenshotApp = new MediaSource
            {
                Source = "Microsoft.ScreenSketch_8wekyb3d8bbwe!App",
                IsEnabled = false
            };
            if (!Settings.MediaSourceList.Any(source => source.Source == "Microsoft.ScreenSketch_8wekyb3d8bbwe!App"))
            {
                Settings.MediaSourceList.Add(screenshotApp);
                SaveSettings();
            }
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
            var currentSource = _mediaService.CurrentMediaInfo?.SourceApp;
            if (currentSource == null)
            {
                CommonDialog.ShowError("未检测到正在播放的媒体，请播放媒体后再试。");
                return;
            }
            if (AddMediaSourceIfMissing(currentSource))
            {
                return;
            }   

            CommonDialog.ShowError("列表已存在该媒体源，");
        }

        private bool AddMediaSourceIfMissing(string currentSource)
        {
            if (Settings.MediaSourceList.Any(source => source.Source == currentSource))
            {
                return false;
            }

            Settings.MediaSourceList.Add(new MediaSource
            {
                Source = currentSource
            });
            SaveSettings();
            return true;
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
                var sourceApp = info.SourceApp;
                var (appName, appIcon) = MediaPlayerData.GetMediaPlayerData(sourceApp);

                await UpdateCurrentMediaUiAsync(() =>
                {
                    CurrentMediaTitle.Text = string.IsNullOrWhiteSpace(info.Title)
                        ? "未知标题"
                        : info.Title;
                    CurrentMediaArtistAlbum.Text =
                        FormatArtistAlbum(info.Artist, info.AlbumTitle);
                    UpdatePlaybackInfo(info.PlaybackInfo);
                    UpdateTimeline(info.Position, info.Duration);
                    CurrentMediaSource.Text = $"播放源：{appName} ({sourceApp})";
                    SetImage(CurrentMediaSourceIcon, appIcon);
                    SetThumbnail(info.Thumbnail);
                });
            }
            catch
            {
                await UpdateCurrentMediaUiAsync(ClearCurrentMediaInfo);
            }
        }

        private async void MediaService_OnMediaPropertiesChanged(object? sender, MediaInfo? info)
        {
            if (info != null)
            {
                await UpdateCurrentMediaUiAsync(() => AddMediaSourceIfMissing(info.SourceApp));
            }

            await RefreshCurrentMediaInfoAsync(info);
        }

        private void MediaService_OnPlaybackStateChanged(
            object? sender,
            GlobalSystemMediaTransportControlsSessionPlaybackInfo playbackInfo)
        {
            _ = UpdateCurrentMediaUiAsync(() => UpdatePlaybackInfo(playbackInfo));
        }

        private void MediaService_OnTimelinePropertyChanged(
            object? sender,
            GlobalSystemMediaTransportControlsSessionTimelineProperties timeline)
        {
            _ = UpdateCurrentMediaUiAsync(() => UpdateTimeline(timeline.Position, timeline.EndTime));
        }

        private async void MediaService_OnFocusedSessionChanged(object? sender, EventArgs e)
        {
            await RefreshCurrentMediaInfoAsync(_mediaService.CurrentMediaInfo);
        }

        private void ClearCurrentMediaInfo()
        {
            CurrentMediaTitle.Text = "未检测到正在播放的媒体";
            CurrentMediaArtistAlbum.Text = "播放媒体后会在此处显示标题、艺术家、专辑与进度。";
            CurrentMediaStatus.Text = "无媒体";
            CurrentMediaStatusIcon.Kind = PackIconKind.HelpCircleOutline;
            CurrentMediaTimeline.Text = "00:00 / 00:00";
            CurrentMediaSource.Text = "播放源：-";
            CurrentMediaSourceIcon.Source = null;
            CurrentMediaSourceIcon.Visibility = Visibility.Collapsed;
            SetThumbnail(null);
        }

        private static string FormatArtistAlbum(string? artist, string? album)
        {
            var displayArtist = string.IsNullOrWhiteSpace(artist) ? "未知艺术家" : artist;
            var displayAlbum = string.IsNullOrWhiteSpace(album) ? "未知专辑" : album;
            return $"{displayArtist} - {displayAlbum}";
        }

        private static string FormatTimeline(TimeSpan position, TimeSpan endTime)
        {
            return $"{position:mm\\:ss} / {endTime:mm\\:ss}";
        }

        private void UpdatePlaybackInfo(GlobalSystemMediaTransportControlsSessionPlaybackInfo? playbackInfo)
        {
            var playbackStatus = playbackInfo?.PlaybackStatus;
            CurrentMediaStatus.Text = GetPlaybackStatusText(playbackStatus);
            CurrentMediaStatusIcon.Kind = GetPlaybackStatusIcon(playbackStatus);
        }

        private void UpdateTimeline(TimeSpan position, TimeSpan endTime)
        {
            CurrentMediaTimeline.Text = FormatTimeline(position, endTime);
        }

        private async Task UpdateCurrentMediaUiAsync(Action update)
        {
            if (_isUnloaded || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return;
            }

            try
            {
                await Dispatcher.InvokeAsync(update);
            }
            catch (TaskCanceledException) when (_isUnloaded || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
            }
            catch (InvalidOperationException) when (_isUnloaded || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
            }
        }

        private static string GetPlaybackStatusText(GlobalSystemMediaTransportControlsSessionPlaybackStatus? status)
        {
            return status switch
            {
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => "正在播放",
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => "已暂停",
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => "已停止",
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing => "切换中",
                _ => "未知状态"
            };
        }

        private static PackIconKind GetPlaybackStatusIcon(GlobalSystemMediaTransportControlsSessionPlaybackStatus? status)
        {
            return status switch
            {
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => PackIconKind.Play,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => PackIconKind.Pause,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => PackIconKind.Stop,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing => PackIconKind.Refresh,
                _ => PackIconKind.HelpCircleOutline
            };
        }

        private static void SetImage(Image image, ImageSource? source)
        {
            image.Source = source;
            image.Visibility = source == null ? Visibility.Collapsed : Visibility.Visible;
        }

        private void SetThumbnail(ImageSource? thumbnail)
        {
            CurrentMediaThumbnail.Source = thumbnail;
            CurrentMediaThumbnail.Visibility = thumbnail == null ? Visibility.Collapsed : Visibility.Visible;
            CurrentMediaThumbnailPlaceholder.Visibility = thumbnail == null ? Visibility.Visible : Visibility.Collapsed;
        }

        private void GeneralSettingsPage_OnUnloaded(object sender, RoutedEventArgs e)
        {
            _isUnloaded = true;
            _mediaService.OnMediaPropertiesChanged -= MediaService_OnMediaPropertiesChanged;
            _mediaService.OnPlaybackStateChanged -= MediaService_OnPlaybackStateChanged;
            _mediaService.OnTimelinePropertyChanged -= MediaService_OnTimelinePropertyChanged;
            _mediaService.OnFocusedSessionChanged -= MediaService_OnFocusedSessionChanged;
        }

        private void DeleteButtonOnClick(object sender,RoutedEventArgs e)
        {
            Button button = (sender as Button)!;
            if (button.DataContext is MediaSource item)
            {
                Settings.MediaSourceList.Remove(item);
                SaveSettings();
            }
        }
        
        private void SaveSettings()
        {
            ConfigureFileHelper.SaveConfig<PluginSettings>(Path.Combine(Plugin.globalConfigFolder!, "Settings.json"), Settings);
        }

        private void SaveButtonOnClick(object sender, RoutedEventArgs e)
        {
            SaveSettings();
        }
    }
}
