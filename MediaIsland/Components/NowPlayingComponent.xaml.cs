using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using MaterialDesignThemes.Wpf;
using MediaIsland.Helpers;
using Microsoft.Extensions.Logging;
using Windows.Media.Control;
using ClassIsland.Shared.Helpers;
using MediaIsland.Models;
using WindowsMediaController;
using static WindowsMediaController.MediaManager;

namespace MediaIsland.Components
{
    [ComponentInfo(
        "6E9C7C44-59EB-499C-A637-2C6C9253BF2B",
        "正在播放",
        PackIconKind.MusicBoxOutline,
        "显示当前播放的媒体信息。"
    )]
    public partial class NowPlayingComponent : ComponentBase<NowPlayingComponentConfig>
    {
        //private string titleLabel, artistLabel, albumLabel, timeLabel, sourceLabel;
        static MediaManager mediaManager = new();

        //TimeSpan currentDuration;
        //TimeSpan currentPosition;
        private ILogger<NowPlayingComponent> Logger { get; }

        private static MediaSession? currentSession = null;

        private PluginSettings globalSettings;

        public NowPlayingComponent(ILogger<NowPlayingComponent> logger)
        {
            InitializeComponent();
            Logger = logger;
            globalSettings =
                ConfigureFileHelper.LoadConfig<PluginSettings>(
                    Path.Combine(Plugin.globalConfigFolder!, "Settings.json"));
        }

        void NowPlayingComponent_OnLoaded(object sender, RoutedEventArgs e)
        {
            Settings.PropertyChanged += OnSettingsPropertyChanged;
            LoadCurrentPlayingInfoAsync();
        }

        void NowPlayingComponent_OnUnloaded(object sender, RoutedEventArgs e)
        {
            Settings.PropertyChanged -= OnSettingsPropertyChanged;
            if (mediaManager.IsStarted) mediaManager.Dispose();
        }

        private async void OnSettingsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "IsHideWhenPaused")
            {
                try
                {
                    GlobalSystemMediaTransportControlsSessionManager _sessionManager =
                        await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                    var playbackInfo = _sessionManager.GetCurrentSession().GetPlaybackInfo();
                    if (playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            MediaGrid.Visibility =
                                Settings.IsHideWhenPaused ? Visibility.Collapsed : Visibility.Visible;
                        });
                    }
                }
                catch
                {
                }
            }

            if (e.PropertyName == "SubInfoType")
            {
                switch (Settings.SubInfoType)
                {
                    case 0:
                        artistText.Visibility = Visibility.Visible;
                        timeText.Visibility = Visibility.Collapsed;
                        break;
                    case 1:
                        if (timeText.Text != "00:00 / 00:00")
                        {
                            artistText.Visibility = Visibility.Collapsed;
                            timeText.Visibility = Visibility.Visible;
                        }

                        break;
                }
            }
        }

        /// <summary>
        /// 获取 SMTC 信息并更新 UI
        /// </summary>
        async void LoadCurrentPlayingInfoAsync()
        {
            mediaManager.OnAnySessionOpened += MediaManager_OnAnySessionOpened;
            mediaManager.OnAnySessionClosed += MediaManager_OnAnySessionClosed;
            mediaManager.OnFocusedSessionChanged += MediaManager_OnFocusedSessionChanged;
            mediaManager.OnAnyPlaybackStateChanged += MediaManager_OnAnyPlaybackStateChanged;
            mediaManager.OnAnyMediaPropertyChanged += MediaManager_OnAnyMediaPropertyChanged;
            mediaManager.OnAnyTimelinePropertyChanged += MediaManager_OnAnyTimelinePropertyChanged;

            try
            {
                if (!mediaManager.IsStarted) await mediaManager.StartAsync();
            }
            catch (COMException)
            {
                Logger!.LogWarning("无法获取 SMTC 会话管理器。");
                await Dispatcher.InvokeAsync(() => { MediaGrid.Visibility = Visibility.Collapsed; });
                return;
            }

            try
            {
                var currentSession = mediaManager.GetFocusedSession();
                Logger!.LogInformation("尝试获取 SMTC 会话信息");
                if (currentSession != null)
                {
                    Logger!.LogInformation("存在 SMTC 会话信息");
                    Logger!.LogDebug("刷新【正在播放】组件内容");
                    await RefreshMediaInfo(currentSession);
                }
                else
                {
                    Logger!.LogInformation("不存在 SMTC 会话信息，隐藏组件 UI");
                    await Dispatcher.InvokeAsync(() => { MediaGrid.Visibility = Visibility.Collapsed; });
                }
            }
            catch (Exception ex)
            {
                Logger!.LogError($"获取 SMTC 会话时发生错误: {ex.Message}");
                await Dispatcher.InvokeAsync(() => { MediaGrid.Visibility = Visibility.Collapsed; });
            }
        }


        /// <summary>
        /// 使用获取到的 SMTC 信息刷新 UI
        /// </summary>
        /// <param name="session">SMTC 会话</param>
        /// <returns></returns>
        private async Task RefreshMediaInfo(MediaSession session)
        {
            if (Settings == null || session?.ControlSession == null) return;
            try
            {
                if (session != null)
                {
                    if (session.ControlSession != null)
                    {
                        try
                        {
                            string sourceApp = session.ControlSession.SourceAppUserModelId;
                            if (IsSourceEnabled(sourceApp, globalSettings.MediaSourceList))
                            {
                                var mediaProperties = await session.ControlSession.TryGetMediaPropertiesAsync();
                                var timeline = session.ControlSession.GetTimelineProperties();
                                var playbackInfo = session.ControlSession.GetPlaybackInfo();
                                Logger!.LogTrace(
                                    $"当前 SMTC 信息：[{sourceApp}] {mediaProperties.Artist} - {mediaProperties.Title} ({playbackInfo.PlaybackStatus}) [{timeline.Position} / {timeline.EndTime}]");
                                await Dispatcher.InvokeAsync(new Action(async () =>
                                {
                                    if (playbackInfo.PlaybackStatus ==
                                        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)
                                    {
                                        MediaGrid.Visibility = Settings.IsHideWhenPaused
                                            ? Visibility.Collapsed
                                            : Visibility.Visible;
                                    }
                                    else
                                    {
                                        MediaGrid.Visibility = Visibility.Visible;
                                        StatusIcon.Kind = PackIconKind.Pause;
                                    }

                                    // 更新播放状态
                                    if (playbackInfo.PlaybackStatus ==
                                        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                                    {
                                        StatusIcon.Kind = PackIconKind.Play;
                                    }
                                    else if (playbackInfo.PlaybackStatus ==
                                             GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)
                                    {
                                        StatusIcon.Kind = PackIconKind.Pause;
                                    }
                                    else if (playbackInfo.PlaybackStatus ==
                                             GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped)
                                    {
                                        StatusIcon.Kind = PackIconKind.Stop;
                                    }
                                    else if (playbackInfo.PlaybackStatus ==
                                             GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing)
                                    {
                                        StatusIcon.Kind = PackIconKind.Refresh;
                                    }

                                    // 更新 UI 内容
                                    titleText.Text = mediaProperties.Title ?? "未知标题";
                                    artistText.Text = mediaProperties.Artist ?? "未知艺术家";
                                    //albumText.Text = mediaProperties.AlbumTitle ?? "未知专辑";

                                    var thumb = mediaProperties.Thumbnail;
                                    if (thumb != null)
                                    {
                                        if (globalSettings.IsCutSpotifyTrademarkEnabled && AppInfoHelper.IsSourceAppSpotify(sourceApp))
                                        {
                                            AlbumArt.ImageSource =
                                                await ThumbnailHelper.GetThumbnail(thumb, isSourceAppSpotify: true);
                                        }
                                        else
                                        {
                                            AlbumArt.ImageSource = await ThumbnailHelper.GetThumbnail(thumb);
                                        }

                                        CoverPlaceholder.Visibility = Visibility.Collapsed;
                                    }
                                    else
                                    {
                                        AlbumArt.ImageSource = null;
                                        CoverPlaceholder.Visibility = Visibility.Visible;
                                    }

                                    (string appName, ImageSource? appIcon) =
                                        MediaPlayerData.GetMediaPlayerData(sourceApp);
                                    sourceText.Text = appName;
                                    sourceIcon.ImageSource = appIcon;

                                    // 进度处理
                                    //UpdateProgressUI(timeline.Position, timeline.EndTime);
                                    //progressBar.Maximum = (int)timeline.EndTime.TotalSeconds;
                                    //progressBar.Value = (int)timeline.Position.TotalSeconds;
                                    timeText.Text = $"{timeline.Position:mm\\:ss} / {timeline.EndTime:mm\\:ss}";
                                    // 更新 UI 时处理时间轴
                                    if (Settings.SubInfoType == 1)
                                    {
                                        if (timeline.Position != timeline.EndTime)
                                        {
                                            artistText.Visibility = Visibility.Collapsed;
                                            timeText.Visibility = Visibility.Visible;
                                        }
                                        else
                                        {
                                            artistText.Visibility = Visibility.Visible;
                                            timeText.Visibility = Visibility.Collapsed;
                                        }
                                    }
                                }));
                            }
                            else
                            {
                                Logger!.LogInformation("当前 SMTC 会话 [{sourceApp}] 已禁用，自动隐藏", sourceApp);
                                await Dispatcher.InvokeAsync(() => { MediaGrid.Visibility = Visibility.Collapsed; });
                            }
                        }
                        catch

                        {
                            Logger!.LogWarning("SMTC 会话为空，无法获取信息");
                            await Dispatcher.InvokeAsync(() => { MediaGrid.Visibility = Visibility.Collapsed; });
                        }
                    }

                    else
                    {
                        Logger!.LogWarning("SMTC 会话为空，无法获取信息");
                        await Dispatcher.InvokeAsync(() => { MediaGrid.Visibility = Visibility.Collapsed; });
                    }
                }
                else
                {
                    await Dispatcher.InvokeAsync(() => { MediaGrid.Visibility = Visibility.Collapsed; });
                }
            }
            catch (Exception ex)
            {
                Logger!.LogError($"获取 SMTC 信息失败：{ex.Message}");
            }
            
        }

        //private void UpdateProgressUI(TimeSpan position, TimeSpan duration)
        //{
        //    currentProgressBar.Value = (int)Math.Min(position.TotalSeconds, progressBar.Maximum);
        //    timeText.Text = $"{position:mm\\:ss} / {duration:mm\\:ss}";

        //    if (duration.TotalSeconds > 0)
        //    {
        //        double ratio = position.TotalSeconds / duration.TotalSeconds;
        //    }
        //}

        /// <summary>
        /// SMTC 会话打开事件
        /// </summary>
        /// <param name="sender">发出事件的 SMTC 会话</param>
        async void MediaManager_OnAnySessionOpened(MediaSession sender)
        {
            Logger.LogDebug($"新 SMTC 会话：{sender.Id}");
            await RefreshMediaInfo(sender);
        }

        /// <summary>
        /// SMTC 会话关闭事件
        /// </summary>
        /// <param name="sender">发出事件的 SMTC 会话</param>
        void MediaManager_OnAnySessionClosed(MediaManager.MediaSession sender)
        {
            Logger!.LogDebug($"SMTC 会话关闭：{sender.Id}");
            sender.MediaManagerInstance.ForceUpdate();
            //await RefreshMediaInfo(sender);
        }

        /// <summary>
        /// SMTC 会话焦点改变事件
        /// </summary>
        /// <param name="sender">发出事件的 SMTC 会话</param>
        async void MediaManager_OnFocusedSessionChanged(MediaSession sender)
        {
            Logger!.LogDebug($"SMTC 会话焦点改变：{sender?.ControlSession?.SourceAppUserModelId}");
            if (sender?.ControlSession == null)
            {
                // 无会话时隐藏 UI
                await Dispatcher.InvokeAsync(() => { MediaGrid.Visibility = Visibility.Collapsed; });
            }
            else
            {
                try
                {
                    await RefreshMediaInfo(sender);
                }
                catch
                {
                    // 刷新失败时隐藏 UI
                    await Dispatcher.InvokeAsync(() => { MediaGrid.Visibility = Visibility.Collapsed; });
                }
            }
        }

        /// <summary>
        /// SMTC 播放状态改变事件
        /// </summary>
        /// <param name="sender">发出事件的 SMTC 会话</param>
        async void MediaManager_OnAnyPlaybackStateChanged(MediaSession sender,
            GlobalSystemMediaTransportControlsSessionPlaybackInfo args)
        {
            Logger!.LogDebug($"SMTC 播放状态改变：{sender.Id} is now {args.PlaybackStatus}");
            if (args.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)
            {
                Dispatcher.Invoke(() =>
                {
                    MediaGrid.Visibility = Settings.IsHideWhenPaused ? Visibility.Collapsed : Visibility.Visible;
                });
            }
            else
            {
                await RefreshMediaInfo(sender);
            }
        }

        /// <summary>
        /// SMTC 媒体属性改变事件
        /// </summary>
        /// <param name="sender">发出事件的 SMTC 会话</param>
        async void MediaManager_OnAnyMediaPropertyChanged(MediaSession sender,
            GlobalSystemMediaTransportControlsSessionMediaProperties args)
        {
            Logger!.LogDebug(
                $"SMTC 媒体属性改变：{sender.Id} is now playing {args.Title} {(string.IsNullOrEmpty(args.Artist) ? "" : $"by {args.Artist}")}");
            await RefreshMediaInfo(sender);
        }

        /// <summary>
        /// SMTC 时间属性改变事件
        /// </summary>
        /// <param name="sender">发出事件的 SMTC 会话</param>
        async void MediaManager_OnAnyTimelinePropertyChanged(MediaSession sender,
            GlobalSystemMediaTransportControlsSessionTimelineProperties args)
        {
            //Logger!.LogDebug($"SMTC 时间属性改变：{sender.Id} timeline is now {args.Position}/{args.EndTime}");
            //await RefreshMediaInfo(sender);
        }

        private bool IsSourceEnabled(string appUserModelId, IEnumerable<MediaSource> sources)
        {
            foreach (var source in sources)
            {
                try
                {
                    if (appUserModelId.Equals(source.Source))
                    {
                        return source.IsEnabled;
                    }
                }
                catch
                {
                    // ignored
                }
            }

            return true;
        }
    }
}