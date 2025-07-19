using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using Microsoft.Extensions.Logging;
using Windows.Media.Control;
using WindowsMediaController;
// ReSharper disable ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
// ReSharper disable ConditionalAccessQualifierIsNonNullableAccordingToAPIContract

namespace MediaIsland.Components
{
    [ComponentInfo(
            "038A3FB9-946A-48B6-871C-11FFDC2BA09C",
            "正在播放(简)",
            "\uEBC9",
            "简略显示当前播放的媒体。"
        )]
    // ReSharper disable once ClassNeverInstantiated.Global
    public partial class SimplyNowPlayingComponent : ComponentBase<SimplyNowPlayingComponentConfig>
    {
        //private string titleLabel, artistLabel, albumLabel, timeLabel, sourceLabel;
        private static readonly MediaManager MediaManager = new();
        //TimeSpan currentDuration;
        //TimeSpan currentPosition;
        private ILogger<SimplyNowPlayingComponent> Logger { get; }


        public SimplyNowPlayingComponent(ILogger<SimplyNowPlayingComponent> logger)
        {
            InitializeComponent();
            Logger = logger;
        }

        private void SimplyNowPlayingComponent_OnLoaded(object sender, RoutedEventArgs e)
        {
            Settings.PropertyChanged += OnSettingsPropertyChanged;
            LoadCurrentPlayingInfoAsync();
        }

        private void SimplyNowPlayingComponent_OnUnloaded(object sender, RoutedEventArgs e)
        {
            Settings.PropertyChanged -= OnSettingsPropertyChanged;
            MediaManager.Dispose();
        }

        // ReSharper disable once AsyncVoidMethod
        private async void OnSettingsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case "IsHideWhenPaused":
                    try
                    {
                        var sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                        var playbackInfo = sessionManager.GetCurrentSession().GetPlaybackInfo();
                        if (playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)
                        {
                            await Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                MediaGrid.IsVisible = !Settings.IsHideWhenPaused;
                            });
                        }
                    }
                    catch
                    {
                        // ignored
                    }

                    break;
                case "InfoType":
                    switch (Settings.InfoType)
                    {
                        case 0:
                            DividerText.IsVisible = true;
                            ArtistText.IsVisible = true;
                            Grid.SetColumn(TitleText, 2);
                            Grid.SetColumn(ArtistText, 0);
                            break;
                        case 1:
                            DividerText.IsVisible = true;
                            ArtistText.IsVisible = true;
                            Grid.SetColumn(TitleText, 0);
                            Grid.SetColumn(ArtistText, 2);
                            break;
                        case 2:
                            DividerText.IsVisible = false;
                            ArtistText.IsVisible = false;
                            break;
                    }
                    Settings.IsDualLineStyle = (Settings.InfoType == 3);
                    break;
            }
        }

        /// <summary>
        /// 获取 SMTC 信息并更新 UI
        /// </summary>
        // ReSharper disable once AsyncVoidMethod
        private async void LoadCurrentPlayingInfoAsync()
        {
            MediaManager.OnAnySessionOpened += MediaManager_OnAnySessionOpened;
            MediaManager.OnAnySessionClosed += MediaManager_OnAnySessionClosed;
            MediaManager.OnFocusedSessionChanged += MediaManager_OnFocusedSessionChanged;
            MediaManager.OnAnyPlaybackStateChanged += MediaManager_OnAnyPlaybackStateChanged;
            MediaManager.OnAnyMediaPropertyChanged += MediaManager_OnAnyMediaPropertyChanged;

            try
            {
                await MediaManager.StartAsync();
            }
            catch (COMException)
            {
                Logger.LogWarning("无法获取 SMTC 会话管理器。");
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    MediaGrid.IsVisible = false;
                });
                return;
            }
            try
            {
                var currentSession = MediaManager.GetFocusedSession();
                Logger.LogInformation("尝试获取 SMTC 会话信息");
                if (currentSession != null)
                {
                    Logger.LogInformation("存在 SMTC 会话信息");
                    Logger.LogDebug("刷新【正在播放】组件内容");
                    await RefreshMediaInfo(currentSession);
                }
                else
                {
                    Logger.LogInformation("不存在 SMTC 会话信息，隐藏组件 UI");
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        MediaGrid.IsVisible = false;
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("获取 SMTC 会话时发生错误: {ExMessage}", ex.Message);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    MediaGrid.IsVisible = false;
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
                            Logger.LogTrace("当前 SMTC 信息：{Artist} - {Title} ({PlaybackStatus}) [{TimelinePosition} / {TimelineEndTime}]", mediaProperties.Artist, mediaProperties.Title, playbackInfo.PlaybackStatus, timeline.Position, timeline.EndTime);

                            await Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                if (playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)
                                {
                                    MediaGrid.IsVisible = !Settings.IsHideWhenPaused;
                                }
                                else
                                {
                                    MediaGrid.IsVisible = true;
                                }

                                StatusIcon.Glyph = playbackInfo.PlaybackStatus switch
                                {
                                    // 更新播放状态
                                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => "\uEDB8",
                                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => "\uEC90",
                                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => "\uF086",
                                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing => "\uE0B4",
                                    _ => StatusIcon.Glyph
                                };

                                // 更新 UI 内容
                                TitleText.Text = mediaProperties.Title ?? "未知标题";
                                ArtistText.Text = mediaProperties.Artist ?? "未知艺术家";
                                //albumText.Text = mediaProperties.AlbumTitle ?? "未知专辑";

                                DualTitleText.Text = mediaProperties.Title ?? "未知标题";
                                DualArtistText.Text = mediaProperties.Artist ?? "未知艺术家";

                                switch (Settings.InfoType)
                                {
                                    case 0:
                                        DividerText.IsVisible = true;
                                        ArtistText.IsVisible = true;
                                        Grid.SetColumn(TitleText, 2);
                                        Grid.SetColumn(ArtistText, 0);
                                        break;
                                    case 1:
                                        DividerText.IsVisible = true;
                                        ArtistText.IsVisible = true;
                                        Grid.SetColumn(TitleText, 0);
                                        Grid.SetColumn(ArtistText, 2);
                                        break;
                                    case 2:
                                        DividerText.IsVisible = false;
                                        ArtistText.IsVisible = false;
                                        Grid.SetColumn(TitleText, 0);
                                        Grid.SetColumn(ArtistText, 2);
                                        break;
                                }
                            });
                        }
                        catch
                        {
                            Logger.LogWarning("SMTC 会话为空，无法获取信息");
                            await Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                MediaGrid.IsVisible = false;
                            });
                        }
                    }
                    else
                    {
                        Logger.LogWarning("SMTC 会话为空，无法获取信息");
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            MediaGrid.IsVisible = false;
                        });
                    }
                }
                else
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        MediaGrid.IsVisible = false;
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("获取 SMTC 信息失败：{ExMessage}", ex.Message);
            }
        }
        /// <summary>
        /// SMTC 会话打开事件
        /// </summary>
        /// <param name="sender">发出事件的 SMTC 会话</param>
        // ReSharper disable once AsyncVoidMethod
        private async void MediaManager_OnAnySessionOpened(MediaManager.MediaSession sender)
        {
            Logger.LogDebug($"新 SMTC 会话：{sender.Id}");
            await RefreshMediaInfo(sender);
        }

        /// <summary>
        /// SMTC 会话关闭事件
        /// </summary>
        /// <param name="sender">发出事件的 SMTC 会话</param>
        private void MediaManager_OnAnySessionClosed(MediaManager.MediaSession sender)
        {
            Logger.LogDebug("SMTC 会话关闭：{SenderId}", sender.Id);
            //await RefreshMediaInfo(sender);
        }
        /// <summary>
        /// SMTC 会话焦点改变事件
        /// </summary>
        /// <param name="sender">发出事件的 SMTC 会话</param>
        // ReSharper disable once AsyncVoidMethod
        private async void MediaManager_OnFocusedSessionChanged(MediaManager.MediaSession sender)
        {
            Logger.LogDebug("SMTC 会话焦点改变：{ControlSessionSourceAppUserModelId}", sender?.ControlSession?.SourceAppUserModelId);
            if (sender?.ControlSession == null)
            {
                // 无会话时隐藏 UI
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    MediaGrid.IsVisible = false;
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
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        MediaGrid.IsVisible = false;
                    });
                }
            }
        }

        /// <summary>
        /// SMTC 播放状态改变事件
        /// </summary>
        /// <param name="sender">发出事件的 SMTC 会话</param>
        /// <param name="args">事件参数</param>
        // ReSharper disable once AsyncVoidMethod
        private async void MediaManager_OnAnyPlaybackStateChanged(MediaManager.MediaSession sender, GlobalSystemMediaTransportControlsSessionPlaybackInfo args)
        {
            Logger.LogDebug("SMTC 播放状态改变：{SenderId} is now {PlaybackStatus}", sender.Id, args.PlaybackStatus);
            if (args.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)
            {
                Dispatcher.UIThread.Invoke(() =>
                {
                    MediaGrid.IsVisible = !Settings.IsHideWhenPaused;
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
        /// <param name="args">事件参数</param>
        // ReSharper disable once AsyncVoidMethod
        private async void MediaManager_OnAnyMediaPropertyChanged(MediaManager.MediaSession sender, GlobalSystemMediaTransportControlsSessionMediaProperties args)
        {
            Logger.LogDebug("SMTC 媒体属性改变：{SenderId} is now playing {Title} {ProbableArtist}", sender.Id, args.Title, string.IsNullOrEmpty(args.Artist) ? "" : $"by {args.Artist}");
            await RefreshMediaInfo(sender);
        }
    }
}