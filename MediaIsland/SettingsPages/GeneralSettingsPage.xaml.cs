using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Windows.Media.Control;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Controls.CommonDialog;
using ClassIsland.Core.Enums.SettingsWindow;
using ClassIsland.Shared.Helpers;
using MaterialDesignThemes.Wpf;
using MediaIsland.Helpers;
using MediaIsland.Models;

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
        private GlobalSystemMediaTransportControlsSessionManager sessionManager;
        private GlobalSystemMediaTransportControlsSession? currentSession;

        public GeneralSettingsPage(Plugin plugin)
        {
            Plugin = plugin;
            Settings = Plugin.Settings;
            InitializeComponent();
            DataContext = this;
            sessionManager = GlobalSystemMediaTransportControlsSessionManager.RequestAsync().GetAwaiter().GetResult();
            sessionManager.SessionsChanged += OnSessionsChanged;
            _ = RefreshCurrentMediaInfoAsync();
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

        private async void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
        {
            await RefreshCurrentMediaInfoAsync();
            var session = sessionManager.GetCurrentSession();
            if (session == null) return;

            var currentSource = session.SourceAppUserModelId;
            var sourceItem = new MediaSource
            {
                Source = currentSource
            };
            if (Settings.MediaSourceList.Any(source => source.Source == currentSource)) return;
            Settings.MediaSourceList.Add(sourceItem);
            SaveSettings();
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
            if (sessionManager.GetCurrentSession() == null)
            {
                CommonDialog.ShowError("未检测到正在播放的媒体，请播放媒体后再试。");
                return;
            }
            var currentSource = sessionManager.GetCurrentSession().SourceAppUserModelId;
            var sourceItem = new MediaSource
            {
                Source = currentSource
            };
            if (Settings.MediaSourceList.All(source => source.Source != currentSource))
            {
                Settings.MediaSourceList.Add(sourceItem);
                SaveSettings();
            }   
            else
            {
                CommonDialog.ShowError("列表已存在该媒体源，");
            }
        }

        private async Task RefreshCurrentMediaInfoAsync()
        {
            var session = sessionManager.GetCurrentSession();
            UpdateCurrentSession(session);

            if (session == null)
            {
                await Dispatcher.InvokeAsync(ClearCurrentMediaInfo);
                return;
            }

            try
            {
                var sourceApp = session.SourceAppUserModelId;
                var mediaProperties = await session.TryGetMediaPropertiesAsync();
                var timeline = session.GetTimelineProperties();
                var playbackInfo = session.GetPlaybackInfo();
                var (appName, appIcon) = MediaPlayerData.GetMediaPlayerData(sourceApp);
                var thumbnail = await ThumbnailHelper.GetThumbnail(mediaProperties.Thumbnail,
                    AppInfoHelper.IsSourceAppSpotify(sourceApp));

                await Dispatcher.InvokeAsync(() =>
                {
                    CurrentMediaTitle.Text = string.IsNullOrWhiteSpace(mediaProperties.Title)
                        ? "未知标题"
                        : mediaProperties.Title;
                    CurrentMediaArtistAlbum.Text =
                        FormatArtistAlbum(mediaProperties.Artist, mediaProperties.AlbumTitle);
                    CurrentMediaStatus.Text = GetPlaybackStatusText(playbackInfo.PlaybackStatus);
                    CurrentMediaStatusIcon.Kind = GetPlaybackStatusIcon(playbackInfo.PlaybackStatus);
                    CurrentMediaTimeline.Text = FormatTimeline(timeline.Position, timeline.EndTime);
                    CurrentMediaSource.Text = $"播放源：{appName} ({sourceApp})";
                    SetImage(CurrentMediaSourceIcon, appIcon);
                    SetThumbnail(thumbnail);
                });
            }
            catch
            {
                await Dispatcher.InvokeAsync(ClearCurrentMediaInfo);
            }
        }

        private void UpdateCurrentSession(GlobalSystemMediaTransportControlsSession? session)
        {
            if (ReferenceEquals(currentSession, session)) return;

            if (currentSession != null)
            {
                currentSession.MediaPropertiesChanged -= CurrentSession_OnMediaPropertiesChanged;
                currentSession.PlaybackInfoChanged -= CurrentSession_OnPlaybackInfoChanged;
                currentSession.TimelinePropertiesChanged -= CurrentSession_OnTimelinePropertiesChanged;
            }

            currentSession = session;

            if (currentSession != null)
            {
                currentSession.MediaPropertiesChanged += CurrentSession_OnMediaPropertiesChanged;
                currentSession.PlaybackInfoChanged += CurrentSession_OnPlaybackInfoChanged;
                currentSession.TimelinePropertiesChanged += CurrentSession_OnTimelinePropertiesChanged;
            }
        }

        private async void CurrentSession_OnMediaPropertiesChanged(
            GlobalSystemMediaTransportControlsSession sender,
            MediaPropertiesChangedEventArgs args)
        {
            await RefreshCurrentMediaInfoAsync();
        }

        private async void CurrentSession_OnPlaybackInfoChanged(
            GlobalSystemMediaTransportControlsSession sender,
            PlaybackInfoChangedEventArgs args)
        {
            await RefreshCurrentMediaInfoAsync();
        }

        private async void CurrentSession_OnTimelinePropertiesChanged(
            GlobalSystemMediaTransportControlsSession sender,
            TimelinePropertiesChangedEventArgs args)
        {
            await RefreshCurrentMediaInfoAsync();
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

        private static string GetPlaybackStatusText(GlobalSystemMediaTransportControlsSessionPlaybackStatus status)
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

        private static PackIconKind GetPlaybackStatusIcon(GlobalSystemMediaTransportControlsSessionPlaybackStatus status)
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
            sessionManager.SessionsChanged -= OnSessionsChanged;
            UpdateCurrentSession(null);
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
