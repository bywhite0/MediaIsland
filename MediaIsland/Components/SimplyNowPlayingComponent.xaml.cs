using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using MaterialDesignThemes.Wpf;
using Microsoft.Extensions.Logging;
using Windows.Media.Control;
using WindowsMediaController;
using static WindowsMediaController.MediaManager;

namespace MediaIsland.Components
{
    [ComponentInfo(
            "038A3FB9-946A-48B6-871C-11FFDC2BA09C",
            "正在播放(简)",
            PackIconKind.MusicBoxOutline,
            "简略显示当前播放的媒体。"
        )]
    public partial class SimplyNowPlayingComponent : ComponentBase<SimplyNowPlayingComponentConfig>
    {
        //private string titleLabel, artistLabel, albumLabel, timeLabel, sourceLabel;
        static MediaManager mediaManager = new();
        //TimeSpan currentDuration;
        //TimeSpan currentPosition;
        private ILogger<SimplyNowPlayingComponent> Logger { get; }

        private static MediaSession? currentSession = null;

        public SimplyNowPlayingComponent(ILogger<SimplyNowPlayingComponent> logger)
        {
            InitializeComponent();
            Logger = logger;
        }

        void SimplyNowPlayingComponent_OnLoaded(object sender, RoutedEventArgs e)
        {
            Settings.PropertyChanged += OnSettingsPropertyChanged;
            LoadCurrentPlayingInfoAsync();
        }

        void SimplyNowPlayingComponent_OnUnloaded(object sender, RoutedEventArgs e)
        {
            Settings.PropertyChanged -= OnSettingsPropertyChanged;
            mediaManager.Dispose();
        }

        private async void OnSettingsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "IsHideWhenPaused")
            {
                try
                {
                    GlobalSystemMediaTransportControlsSessionManager _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                    var playbackInfo = _sessionManager.GetCurrentSession().GetPlaybackInfo();
                    if (playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            MediaGrid.Visibility = Settings.IsHideWhenPaused ? Visibility.Collapsed : Visibility.Visible;
                        });
                    }
                }
                catch { }
            }
                if (e.PropertyName == "InfoType")
            {
                switch (Settings.InfoType)
                {
                    case 0:
                        dividerText.Visibility = Visibility.Visible;
                        artistText.Visibility = Visibility.Visible;
                        Grid.SetColumn(titleText, 2);
                        Grid.SetColumn(artistText, 0);
                        break;
                    case 1:
                        dividerText.Visibility = Visibility.Visible;
                        artistText.Visibility = Visibility.Visible;
                        Grid.SetColumn(titleText, 0);
                        Grid.SetColumn(artistText, 2);
                        break;
                    case 2:
                        dividerText.Visibility = Visibility.Collapsed;
                        artistText.Visibility = Visibility.Collapsed;
                        break;
                }
                Settings.IsDualLineStyle = (Settings.InfoType == 3);
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

            try
            {
                await mediaManager.StartAsync();
            }
            catch (COMException)
            {
                Logger!.LogWarning("无法获取 SMTC 会话管理器。");
                await Dispatcher.InvokeAsync(() =>
                {
                    MediaGrid.Visibility = Visibility.Collapsed;
                });
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
                    await Dispatcher.InvokeAsync(() =>
                    {
                        MediaGrid.Visibility = Visibility.Collapsed;
                    });
                }
            }
            catch (Exception ex)
            {
                Logger!.LogError($"获取 SMTC 会话时发生错误: {ex.Message}");
                await Dispatcher.InvokeAsync(() =>
                {
                    MediaGrid.Visibility = Visibility.Collapsed;
                });
            }
        }


        /// <summary>
        /// 使用获取到的 SMTC 信息刷新 UI
        /// </summary>
        /// <param name="session">SMTC 会话</param>
        /// <returns></returns>
        private async Task RefreshMediaInfo(MediaManager.MediaSession session)
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
                            var mediaProperties = await session.ControlSession.TryGetMediaPropertiesAsync();
                            var timeline = session.ControlSession.GetTimelineProperties();
                            var playbackInfo = session.ControlSession.GetPlaybackInfo();
                            Logger!.LogTrace($"当前 SMTC 信息：{mediaProperties.Artist} - {mediaProperties.Title} ({playbackInfo.PlaybackStatus}) [{timeline.Position} / {timeline.EndTime}]");

                            await Dispatcher.InvokeAsync(new Action(() =>
                            {
                                if (playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)
                                {
                                    MediaGrid.Visibility = Settings.IsHideWhenPaused ? Visibility.Collapsed : Visibility.Visible;
                                }
                                else
                                {
                                    MediaGrid.Visibility = Visibility.Visible;
                                }
                                // 更新播放状态
                                if (playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                                {
                                    StatusIcon.Kind = PackIconKind.Play;
                                }
                                else if (playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)
                                {
                                    StatusIcon.Kind = PackIconKind.Pause;
                                }
                                else if (playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped)
                                {
                                    StatusIcon.Kind = PackIconKind.Stop;
                                }
                                else if (playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing)
                                {
                                    StatusIcon.Kind = PackIconKind.Refresh;
                                }

                                // 更新 UI 内容
                                titleText.Text = mediaProperties.Title ?? "未知标题";
                                artistText.Text = mediaProperties.Artist ?? "未知艺术家";
                                //albumText.Text = mediaProperties.AlbumTitle ?? "未知专辑";

                                dualTitleText.Text = mediaProperties.Title ?? "未知标题";
                                dualArtistText.Text = mediaProperties.Artist ?? "未知艺术家";

                                switch (Settings.InfoType)
                                {
                                    case 0:
                                        dividerText.Visibility = Visibility.Visible;
                                        artistText.Visibility = Visibility.Visible;
                                        Grid.SetColumn(titleText, 2);
                                        Grid.SetColumn(artistText, 0);
                                        break;
                                    case 1:
                                        dividerText.Visibility = Visibility.Visible;
                                        artistText.Visibility = Visibility.Visible;
                                        Grid.SetColumn(titleText, 0);
                                        Grid.SetColumn(artistText, 2);
                                        break;
                                    case 2:
                                        dividerText.Visibility = Visibility.Collapsed;
                                        artistText.Visibility = Visibility.Collapsed;
                                        Grid.SetColumn(titleText, 0);
                                        Grid.SetColumn(artistText, 2);
                                        break;
                                }
                            }));
                        }
                        catch
                        {
                            Logger!.LogWarning("SMTC 会话为空，无法获取信息");
                            await Dispatcher.InvokeAsync(() =>
                            {
                                MediaGrid.Visibility = Visibility.Collapsed;
                            });
                        }
                    }
                    else
                    {
                        Logger!.LogWarning("SMTC 会话为空，无法获取信息");
                        await Dispatcher.InvokeAsync(() =>
                        {
                            MediaGrid.Visibility = Visibility.Collapsed;
                        });
                    }
                }
                else
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        MediaGrid.Visibility = Visibility.Collapsed;
                    });
                }
            }
            catch (Exception ex)
            {
                Logger!.LogError($"获取 SMTC 信息失败：{ex.Message}");
            }
        }
        /// <summary>
        /// SMTC 会话打开事件
        /// </summary>
        /// <param name="sender">发出事件的 SMTC 会话</param>
        async void MediaManager_OnAnySessionOpened(MediaManager.MediaSession sender)
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
            //await RefreshMediaInfo(sender);
        }
        /// <summary>
        /// SMTC 会话焦点改变事件
        /// </summary>
        /// <param name="sender">发出事件的 SMTC 会话</param>
        async void MediaManager_OnFocusedSessionChanged(MediaManager.MediaSession sender)
        {
            Logger!.LogDebug($"SMTC 会话焦点改变：{sender?.ControlSession?.SourceAppUserModelId}");
            if (sender?.ControlSession == null)
            {
                // 无会话时隐藏 UI
                await Dispatcher.InvokeAsync(() =>
                {
                    MediaGrid.Visibility = Visibility.Collapsed;
                });
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
                    await Dispatcher.InvokeAsync(() =>
                    {
                        MediaGrid.Visibility = Visibility.Collapsed;
                    });
                }
            }
        }
        /// <summary>
        /// SMTC 播放状态改变事件
        /// </summary>
        /// <param name="sender">发出事件的 SMTC 会话</param>
        async void MediaManager_OnAnyPlaybackStateChanged(MediaManager.MediaSession sender, GlobalSystemMediaTransportControlsSessionPlaybackInfo args)
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
        async void MediaManager_OnAnyMediaPropertyChanged(MediaManager.MediaSession sender, GlobalSystemMediaTransportControlsSessionMediaProperties args)
        {
            Logger!.LogDebug($"SMTC 媒体属性改变：{sender.Id} is now playing {args.Title} {(string.IsNullOrEmpty(args.Artist) ? "" : $"by {args.Artist}")}");
            await RefreshMediaInfo(sender);
        }
    }
}