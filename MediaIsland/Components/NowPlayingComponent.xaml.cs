using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using MaterialDesignThemes.Wpf;
using MediaIsland.Helpers;
using Microsoft.Extensions.Logging;
using Windows.Media.Control;
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
        static MediaManager? mediaManager;
        //TimeSpan currentDuration;
        //TimeSpan currentPosition;
        private ILogger<NowPlayingComponent> Logger { get; }

        private static MediaSession? currentSession = null;

        public NowPlayingComponent(ILogger<NowPlayingComponent> logger)
        {
            InitializeComponent();
            Logger = logger;
        }

        void NowPlayingComponent_OnLoaded(object sender, RoutedEventArgs e)
        {
            Settings.PropertyChanged += OnSettingsPropertyChanged;
            LoadCurrentPlayingInfoAsync();
        }

        void NowPlayingComponent_OnUnloaded(object sender, RoutedEventArgs e)
        {
            Settings.PropertyChanged -= OnSettingsPropertyChanged;
        }

        private async void OnSettingsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "IsHideWhenPaused")
            {
                if (currentSession?.ControlSession?.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        MediaGrid.Visibility = Settings.IsHideWhenPaused ? Visibility.Collapsed : Visibility.Visible;
                    });
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
            mediaManager = new MediaManager();
            mediaManager.OnAnySessionOpened += MediaManager_OnAnySessionOpened;
            mediaManager.OnAnySessionClosed += MediaManager_OnAnySessionClosed;
            mediaManager.OnFocusedSessionChanged += MediaManager_OnFocusedSessionChanged;
            mediaManager.OnAnyPlaybackStateChanged += MediaManager_OnAnyPlaybackStateChanged;
            mediaManager.OnAnyMediaPropertyChanged += MediaManager_OnAnyMediaPropertyChanged;
            mediaManager.OnAnyTimelinePropertyChanged += MediaManager_OnAnyTimelinePropertyChanged;

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
                            var mediaProperties = await session.ControlSession.TryGetMediaPropertiesAsync();
                            var timeline = session.ControlSession.GetTimelineProperties();
                            var playbackInfo = session.ControlSession.GetPlaybackInfo();
                            Logger!.LogTrace($"当前 SMTC 信息：[{sourceApp}] {mediaProperties.Artist} - {mediaProperties.Title} ({playbackInfo.PlaybackStatus}) [{timeline.Position} / {timeline.EndTime}]");
                            await Dispatcher.InvokeAsync(async () =>
                            {
                                sourceText.Text = await AppInfoHelper.GetFriendlyAppNameAsync(sourceApp);
                                sourceIcon.ImageSource = IconHelper.GetAppIcon(sourceApp);
                                await RefreshMediaProperties(session);
                                await RefreshPlaybackInfo(session);
                                await RefreshTimelineProperties(session);
                            });
                            
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

        async Task RefreshMediaProperties(MediaSession session)
        {
            if (session != null)
            {
                if (session.ControlSession != null)
                {
                    try
                    {
                        string sourceApp = session.ControlSession.SourceAppUserModelId;
                        var mediaProperties = await session.ControlSession.TryGetMediaPropertiesAsync();
                        var thumb = mediaProperties.Thumbnail;
                        Dispatcher.Invoke(() =>
                        {
                            // 更新标题、艺术家
                            titleText.Text = mediaProperties.Title ?? "未知标题";
                            artistText.Text = mediaProperties.Artist ?? "未知艺术家";
                            //albumText.Text = mediaProperties.AlbumTitle ?? "未知专辑";
                        });

                        await Dispatcher.InvokeAsync(new Action(async () =>
                        {
                            // 更新封面
                            if (thumb != null)
                            {
                                if (AppInfoHelper.IsSourceAppSpotify(sourceApp))
                                {
                                    AlbumArt.ImageSource = await ThumbnailHelper.GetThumbnail(thumb, isSourceAppSpotify: true);
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
                        }));
                    }
                    catch (Exception ex)
                    {
                        Logger!.LogWarning($"无法获取媒体属性：{ex.Message}");
                        return;
                    }
                }
                else
                {
                    Logger!.LogWarning("SMTC 会话为空，无法获取媒体属性");
                    return;
                }
            }
            else
            {
                Logger!.LogWarning("SMTC 会话为空，无法获取媒体属性");
                return;
            }
        }

        async Task RefreshPlaybackInfo(MediaSession session)
        {
            if (session != null)
            {
                if (session.ControlSession != null)
                {
                    try
                    {
                        var playbackInfo = session.ControlSession.GetPlaybackInfo();
                        await Dispatcher.InvokeAsync(new Action(() =>
                        {
                            // 更新播放状态
                            if (playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                            {
                                MediaGrid.Visibility = Visibility.Visible;
                                StatusIcon.Kind = PackIconKind.Play;
                            }
                            else if (playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)
                            {
                                StatusIcon.Kind = PackIconKind.Pause;
                                MediaGrid.Visibility = Settings.IsHideWhenPaused ? Visibility.Collapsed : Visibility.Visible;
                            }
                            else if (playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped)
                            {
                                StatusIcon.Kind = PackIconKind.Stop;
                            }
                            else if (playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing)
                            {
                                StatusIcon.Kind = PackIconKind.Refresh;
                            }
                        }));
                    }
                    catch (Exception ex)
                    {
                        Logger!.LogWarning($"无法获取播放状态：{ex.Message}");
                        return;
                    }
                }
                else
                {
                    Logger!.LogWarning("SMTC 会话为空，无法获取播放状态");
                    return;
                }
            }
            else
            {
                Logger!.LogWarning("SMTC 会话为空，无法获取播放状态");
                return;
            }
        }

        async Task RefreshTimelineProperties(MediaSession session)
        {
            if (session != null)
            {
                if (session.ControlSession != null)
                {
                    try
                    {
                        var timeline = session.ControlSession.GetTimelineProperties();
                        await Dispatcher.InvokeAsync(new Action(() =>
                        {
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
                            timeText.Text = $"{timeline.Position:mm\\:ss} / {timeline.EndTime:mm\\:ss}";
                        }));
                    }
                    catch (Exception ex)
                    {
                        Logger!.LogWarning($"无法获取时间轴信息：{ex.Message}");
                        return;
                    }
                }
                else
                {
                    Logger!.LogWarning("SMTC 会话为空，无法获取时间轴信息");
                    return;
                }
            }
            else
            {
                Logger!.LogWarning("SMTC 会话为空，无法获取时间轴信息");
                return;
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
        async void MediaManager_OnAnyPlaybackStateChanged(MediaSession sender, GlobalSystemMediaTransportControlsSessionPlaybackInfo args)
        {
            Logger!.LogDebug($"SMTC 播放状态改变：{sender.Id} is now {args.PlaybackStatus}");
            try
            {
                if (args.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)
                {
                    Dispatcher.Invoke(() =>
                    {
                        MediaGrid.Visibility = Settings.IsHideWhenPaused ? Visibility.Collapsed : Visibility.Visible;
                    });
                    await Dispatcher.InvokeAsync(async () => await RefreshPlaybackInfo(sender));
                }
                else
                {
                    await Dispatcher.InvokeAsync(async () => await RefreshPlaybackInfo(sender));
                }
            }
            catch { }
        }
        /// <summary>
        /// SMTC 媒体属性改变事件
        /// </summary>
        /// <param name="sender">发出事件的 SMTC 会话</param>
        async void MediaManager_OnAnyMediaPropertyChanged(MediaSession sender, GlobalSystemMediaTransportControlsSessionMediaProperties args)
        {
            Logger!.LogDebug($"SMTC 媒体属性改变：{sender.Id} is now playing {args.Title} {(string.IsNullOrEmpty(args.Artist) ? "" : $"by {args.Artist}")}");
            try
            {
                await Dispatcher.InvokeAsync(async () => await RefreshMediaProperties(sender));
            }
            catch { }
        }
        /// <summary>
        /// SMTC 时间属性改变事件
        /// </summary>
        /// <param name="sender">发出事件的 SMTC 会话</param>
        async void MediaManager_OnAnyTimelinePropertyChanged(MediaSession sender, GlobalSystemMediaTransportControlsSessionTimelineProperties args)
        {
            //Logger!.LogDebug($"SMTC 时间属性改变：{sender.Id} timeline is now {args.Position}/{args.EndTime}");
            try
            {
                await Dispatcher.InvokeAsync(async () => await RefreshTimelineProperties(sender));
            }
            catch { }
        }
    }
}