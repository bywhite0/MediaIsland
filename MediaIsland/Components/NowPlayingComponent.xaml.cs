using System.Windows;
using System.Windows.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using MahApps.Metro.Controls;
using MaterialDesignThemes.Wpf;
using MediaIsland.Helpers;
using MediaIsland.Services;
using Microsoft.Extensions.Logging;
using Windows.Media.Control;
using WindowsMediaController;
using static WindowsMediaController.MediaManager;

namespace MediaIsland.Components
{
    [ComponentInfo(
            "6E9C7C44-59EB-499C-A637-2C6C9253BF2B",
            "正在播放",
            PackIconKind.MusicBox,
            "显示当前播放的媒体信息。"
        )]
    public partial class NowPlayingComponent : ComponentBase<NowPlayingComponentConfig>
    {
        //private string titleLabel, artistLabel, albumLabel, timeLabel, sourceLabel;
        //static MediaManager? mediaManager;
        readonly IMediaSessionService _mediaSessionService;
        //TimeSpan currentDuration;
        //TimeSpan currentPosition;
        private ILogger<NowPlayingComponent> ComponentLogger { get; }
        private ILogger<MediaSessionService> ServiceLogger { get; }

        private static MediaSession? currentSession = null;

        public NowPlayingComponent(ILogger<NowPlayingComponent> clogger, ILogger<MediaSessionService> slogger)
        {
            InitializeComponent();
            ComponentLogger = clogger;
            ServiceLogger = slogger;
            _mediaSessionService = new MediaSessionService(slogger);
            _mediaSessionService.StartAsync();
            _mediaSessionService.MediaSessionChanged += MediaSessionChanged;
            Task.Run(async () => await RefreshMediaInfo(_mediaSessionService.CurrentSession));
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
            //mediaManager = new MediaManager();
            //mediaManager.OnAnySessionOpened += MediaManager_OnAnySessionOpened;
            //mediaManager.OnAnySessionClosed += MediaManager_OnAnySessionClosed;
            //mediaManager.OnFocusedSessionChanged += MediaManager_OnFocusedSessionChanged;
            //mediaManager.OnAnyPlaybackStateChanged += MediaManager_OnAnyPlaybackStateChanged;
            //mediaManager.OnAnyMediaPropertyChanged += MediaManager_OnAnyMediaPropertyChanged;
            //mediaManager.OnAnyTimelinePropertyChanged += MediaManager_OnAnyTimelinePropertyChanged;

            //await mediaManager.StartAsync();
            try
            {
                    var currentSession = _mediaSessionService.CurrentSession;
                    ComponentLogger!.LogInformation("尝试获取 SMTC 会话信息");
                if (currentSession != null)
                {
                    ComponentLogger!.LogInformation("存在 SMTC 会话信息");
                    ComponentLogger!.LogDebug("刷新【正在播放】组件内容");
                    await RefreshMediaInfo(currentSession);
                }
                else
                {
                    ComponentLogger!.LogInformation("不存在 SMTC 会话信息，隐藏组件 UI");
                    await Dispatcher.InvokeAsync(() =>
                    {
                        MediaGrid.Visibility = Visibility.Collapsed;
                    });
                }
            }
                catch (Exception ex)
            {
                ComponentLogger!.LogError($"获取 SMTC 会话时发生错误: {ex.Message}");
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
                            string sourceApp = session.ControlSession.SourceAppUserModelId;
                            var mediaProperties = await session.ControlSession.TryGetMediaPropertiesAsync();
                            var timeline = session.ControlSession.GetTimelineProperties();
                            var playbackInfo = session.ControlSession.GetPlaybackInfo();
                            ComponentLogger!.LogTrace($"当前 SMTC 信息：[{sourceApp}] {mediaProperties.Artist} - {mediaProperties.Title} ({playbackInfo.PlaybackStatus}) [{timeline.Position} / {timeline.EndTime}]");

                            await Dispatcher.InvokeAsync(new Action(async () =>
                            {
                                if (playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)
                                {
                                    MediaGrid.Visibility = Settings.IsHideWhenPaused ? Visibility.Collapsed : Visibility.Visible;
                                }
                                else
                                {
                                    MediaGrid.Visibility = Visibility.Visible;
                                }
                                // 强制更新UI元素
                                titleText.Text = mediaProperties.Title ?? "未知标题";
                                artistText.Text = mediaProperties.Artist ?? "未知艺术家";
                                //albumText.Text = mediaProperties.AlbumTitle ?? "未知专辑";

                                var thumb = mediaProperties.Thumbnail;
                                if (thumb != null)
                                {
                                    AlbumArt.ImageSource = await ThumbnailHelper.GetThumbnail(thumb);
                                    CoverPlaceholder.Visibility = Visibility.Collapsed;
                                }
                                else
                                {
                                    AlbumArt.ImageSource = null;
                                    CoverPlaceholder.Visibility = Visibility.Visible;
                                }

                                // 更新播放器信息
                                sourceText.Text = await AppInfoHelper.GetFriendlyAppNameAsync(session.Id);
                                sourceIcon.ImageSource = IconHelper.GetAppIcon(session.Id);

                                // 进度处理
                                //UpdateProgressUI(timeline.Position, timeline.EndTime);
                                //progressBar.Maximum = (int)timeline.EndTime.TotalSeconds;
                                //progressBar.Value = (int)timeline.Position.TotalSeconds;
                                timeText.Text = $"{timeline.Position:mm\\:ss} / {timeline.EndTime:mm\\:ss}";
                            }));
                        }
                        catch
                        {
                            ComponentLogger!.LogWarning("SMTC 会话为空，无法获取信息");
                            await Dispatcher.InvokeAsync(() =>
                            {
                                MediaGrid.Visibility = Visibility.Collapsed;
                            });
                        }
                    }
                    else
                    {
                        ComponentLogger!.LogWarning("SMTC 会话为空，无法获取信息");
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
                ComponentLogger!.LogError($"获取 SMTC 信息失败：{ex.Message}");
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

        async void MediaSessionChanged(Object? sender, MediaSession? session)
        {
            await RefreshMediaInfo(session);
        }

        /// <summary>
        /// SMTC 会话打开事件
        /// </summary>
        /// <param name="sender">发出事件的 SMTC 会话</param>
        void MediaManager_OnAnySessionOpened(MediaManager.MediaSession sender)
        {
            ComponentLogger.LogDebug($"新 SMTC 会话：{sender.Id}");
            //await RefreshMediaInfo(sender);
        }

        /// <summary>
        /// SMTC 会话关闭事件
        /// </summary>
        /// <param name="sender">发出事件的 SMTC 会话</param>
        void MediaManager_OnAnySessionClosed(MediaManager.MediaSession sender)
        {
            ComponentLogger!.LogDebug($"SMTC 会话关闭：{sender.Id}");
            //await RefreshMediaInfo(sender);
        }
        /// <summary>
        /// SMTC 会话焦点改变事件
        /// </summary>
        /// <param name="sender">发出事件的 SMTC 会话</param>
        async void MediaManager_OnFocusedSessionChanged(MediaManager.MediaSession sender)
        {
            ComponentLogger!.LogDebug($"SMTC 会话焦点改变：{sender?.ControlSession?.SourceAppUserModelId}");
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
            ComponentLogger!.LogDebug($"SMTC 播放状态改变：{sender.Id} is now {args.PlaybackStatus}");
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
            ComponentLogger!.LogDebug($"SMTC 媒体属性改变：{sender.Id} is now playing {args.Title} {(string.IsNullOrEmpty(args.Artist) ? "" : $"by {args.Artist}")}");
            await RefreshMediaInfo(sender);
        }
        /// <summary>
        /// SMTC 时间属性改变事件
        /// </summary>
        /// <param name="sender">发出事件的 SMTC 会话</param>
        async void MediaManager_OnAnyTimelinePropertyChanged(MediaManager.MediaSession sender, GlobalSystemMediaTransportControlsSessionTimelineProperties args)
        {
            //ComponentLogger!.LogDebug($"SMTC 时间属性改变：{sender.Id} timeline is now {args.Position}/{args.EndTime}");
            //await RefreshMediaInfo(sender);
        }
    }
}