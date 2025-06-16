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
            PackIconKind.MusicBoxOutline,
            "显示当前播放的媒体信息。"
        )]
    public partial class NowPlayingComponent : ComponentBase<NowPlayingComponentConfig>
    {
        //private string titleLabel, artistLabel, albumLabel, timeLabel, sourceLabel;
        //static MediaManager? mediaManager;
        public IMediaSessionService MediaSessionService;
        //TimeSpan currentDuration;
        //TimeSpan currentPosition;
        private ILogger<NowPlayingComponent> ComponentLogger { get; }

        private static MediaSession? currentSession = null;

        public NowPlayingComponent(ILogger<NowPlayingComponent> clogger, IMediaSessionService mediaSessionService)
        {
            ComponentLogger = clogger;
            MediaSessionService = mediaSessionService;
            MediaSessionService.StartAsync();
            MediaSessionService.MediaSessionChanged += MediaSessionChanged;
            InitializeComponent();
        }

        void NowPlayingComponent_OnLoaded(object sender, RoutedEventArgs e)
        {
            Settings.PropertyChanged += OnSettingsPropertyChanged;
            LoadCurrentPlayingInfoAsync();
        }

        void NowPlayingComponent_OnUnloaded(object sender, RoutedEventArgs e)
        {
            Settings.PropertyChanged -= OnSettingsPropertyChanged;
            MediaSessionService.MediaSessionChanged -= MediaSessionChanged;
            //MediaSessionService.StopAsync();
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
                    var currentSession = MediaSessionService.CurrentSession;
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
                                    StatusIcon.Kind = PackIconKind.Pause;
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

                                var thumb = mediaProperties.Thumbnail;
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

                                // 更新播放器信息
                                sourceText.Text = await AppInfoHelper.GetFriendlyAppNameAsync(session.Id);
                                sourceIcon.ImageSource = IconHelper.GetAppIcon(session.Id);

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
            if (session == null)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    MediaGrid.Visibility = Visibility.Collapsed;
                });
            }
            await RefreshMediaInfo(session);
        }
    }
}