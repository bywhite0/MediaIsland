using System.Runtime.InteropServices;
using Windows.Media.Control;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using MediaIsland.Helpers;
using Microsoft.Extensions.Logging;
using WindowsMediaController;
using static WindowsMediaController.MediaManager;
using RoutedEventArgs = Avalonia.Interactivity.RoutedEventArgs;
// ReSharper disable ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
// ReSharper disable ConditionalAccessQualifierIsNonNullableAccordingToAPIContract

namespace MediaIsland.Components
{
    [ComponentInfo(
            "6E9C7C44-59EB-499C-A637-2C6C9253BF2B",
            "正在播放",
            "\uEBC9",
            "显示当前播放的媒体信息。"
        )]
    // ReSharper disable once ClassNeverInstantiated.Global
    public partial class NowPlayingComponent : ComponentBase<NowPlayingComponentConfig>
    {
        //private string titleLabel, artistLabel, albumLabel, timeLabel, sourceLabel;
        private static readonly MediaManager MediaManager = new();
        //TimeSpan currentDuration;
        //TimeSpan currentPosition;
        private ILogger<NowPlayingComponent> Logger { get; }


        public NowPlayingComponent(ILogger<NowPlayingComponent> logger)
        {
            InitializeComponent();
            Logger = logger;
        }

        private void NowPlayingComponent_OnLoaded(object? sender, RoutedEventArgs routedEventArgs)
        {
            Settings.PropertyChanged += OnSettingsPropertyChanged;
            LoadCurrentPlayingInfoAsync();
        }

        private void NowPlayingComponent_OnUnloaded(object? sender, RoutedEventArgs routedEventArgs)
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
                case "SubInfoType":
                    switch (Settings.SubInfoType)
                    {
                        case 0:
                            ArtistText.IsVisible = true;
                            TimeText.IsVisible = false;
                            break;
                        case 1:
                            if (TimeText.Text != "00:00 / 00:00")
                            {
                                ArtistText.IsVisible = false;
                                TimeText.IsVisible = true;
                            }
                            break;
                    }

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
            MediaManager.OnAnyTimelinePropertyChanged += MediaManager_OnAnyTimelinePropertyChanged;

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
                Logger.LogError($"获取 SMTC 会话时发生错误: {ex.Message}");
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
                            var sourceApp = session.ControlSession.SourceAppUserModelId;
                            var mediaProperties = await session.ControlSession.TryGetMediaPropertiesAsync();
                            var timeline = session.ControlSession.GetTimelineProperties();
                            var playbackInfo = session.ControlSession.GetPlaybackInfo();
                            Logger.LogTrace("当前 SMTC 信息：[{SourceApp}] {Artist} - {Title} ({PlaybackStatus}) [{TimelinePosition} / {TimelineEndTime}]", sourceApp, mediaProperties.Artist, mediaProperties.Title, playbackInfo.PlaybackStatus, timeline.Position, timeline.EndTime);
                            // ReSharper disable once AsyncVoidMethod
                            await Dispatcher.UIThread.InvokeAsync(async void () =>
                            {
                                SourceText.Text = await AppInfoHelper.GetFriendlyAppNameAsync(sourceApp);
                                // SourceIcon.Source = IconHelper.GetAppIcon(sourceApp);
                                await RefreshMediaProperties(session);
                                await RefreshPlaybackInfo(session);
                                await RefreshTimelineProperties(session);
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

        private async Task RefreshMediaProperties(MediaSession session)
        {
            if (session != null)
            {
                if (session.ControlSession != null)
                {
                    try
                    {
                        var sourceApp = session.ControlSession.SourceAppUserModelId;
                        var mediaProperties = await session.ControlSession.TryGetMediaPropertiesAsync();
                        var thumb = mediaProperties.Thumbnail;
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            // 更新标题、艺术家
                            TitleText.Text = mediaProperties.Title ?? "未知标题";
                            ArtistText.Text = mediaProperties.Artist ?? "未知艺术家";
                            //albumText.Text = mediaProperties.AlbumTitle ?? "未知专辑";
                        });

                        // ReSharper disable once AsyncVoidMethod
                        await Dispatcher.UIThread.InvokeAsync(async void () =>
                        {
                            // 更新封面
                            if (thumb != null)
                            {
                                if (AppInfoHelper.IsSourceAppSpotify(sourceApp))
                                {
                                    AlbumArt.Source = await ThumbnailHelper.GetThumbnail(thumb, isSourceAppSpotify: true);
                                }
                                else
                                {
                                    AlbumArt.Source = await ThumbnailHelper.GetThumbnail(thumb);
                                }
                                CoverPlaceholder.IsVisible = false;
                            }
                            else
                            {
                                AlbumArt.Source = null;
                                CoverPlaceholder.IsVisible = true;
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning("无法获取媒体属性：{ExMessage}", ex.Message);
                    }
                }
                else
                {
                    Logger.LogWarning("SMTC 会话为空，无法获取媒体属性");
                }
            }
            else
            {
                Logger.LogWarning("SMTC 会话为空，无法获取媒体属性");
            }
        }

        private async Task RefreshPlaybackInfo(MediaSession session)
        {
            if (session != null)
            {
                if (session.ControlSession != null)
                {
                    try
                    {
                        var playbackInfo = session.ControlSession.GetPlaybackInfo();
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            switch (playbackInfo.PlaybackStatus)
                            {
                                // 更新播放状态
                                case GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing:
                                    MediaGrid.IsVisible = true;
                                    StatusIcon.Glyph = "\uEDB8";
                                    break;
                                case GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused:
                                    StatusIcon.Glyph = "\uEC90";
                                    MediaGrid.IsVisible = !Settings.IsHideWhenPaused;
                                    break;
                                case GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped:
                                    StatusIcon.Glyph = "\uF086";
                                    break;
                                case GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing:
                                    StatusIcon.Glyph = "\uE0B4";
                                    break;
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning("无法获取播放状态：{ExMessage}", ex.Message);
                    }
                }
                else
                {
                    Logger.LogWarning("SMTC 会话为空，无法获取播放状态");
                }
            }
            else
            {
                Logger.LogWarning("SMTC 会话为空，无法获取播放状态");
            }
        }

        private async Task RefreshTimelineProperties(MediaSession session)
        {
            if (session != null)
            {
                if (session.ControlSession != null)
                {
                    try
                    {
                        var timeline = session.ControlSession.GetTimelineProperties();
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            // 更新 UI 时处理时间轴
                            if (Settings.SubInfoType == 1)
                            {
                                if (timeline.Position != timeline.EndTime)
                                {
                                    ArtistText.IsVisible = false;
                                    TimeText.IsVisible = true;
                                }
                                else
                                {
                                    ArtistText.IsVisible = true;
                                    TimeText.IsVisible = false;
                                }
                            }
                            TimeText.Text = (timeline.EndTime.Hours == 0) ? $@"{timeline.Position:mm\:ss} / {timeline.EndTime:mm\:ss}" : $@"{timeline.Position} / {timeline.EndTime}";
                        });
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning("无法获取时间轴信息：{ExMessage}", ex.Message);
                    }
                }
                else
                {
                    Logger.LogWarning("SMTC 会话为空，无法获取时间轴信息");
                }
            }
            else
            {
                Logger.LogWarning("SMTC 会话为空，无法获取时间轴信息");
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
        // ReSharper disable once AsyncVoidMethod
        private async void MediaManager_OnAnySessionOpened(MediaSession sender)
        {
            Logger.LogDebug("新 SMTC 会话：{SenderId}", sender.Id);
            await RefreshMediaInfo(sender);
        }

        /// <summary>
        /// SMTC 会话关闭事件
        /// </summary>
        /// <param name="sender">发出事件的 SMTC 会话</param>
        private void MediaManager_OnAnySessionClosed(MediaSession sender)
        {
            Logger.LogDebug("SMTC 会话关闭：{SenderId}", sender.Id);
            sender.MediaManagerInstance.ForceUpdate();
            //await RefreshMediaInfo(sender);
        }
        /// <summary>
        /// SMTC 会话焦点改变事件
        /// </summary>
        /// <param name="sender">发出事件的 SMTC 会话</param>
        // ReSharper disable once AsyncVoidMethod
        private async void MediaManager_OnFocusedSessionChanged(MediaSession sender)
        {
            Logger.LogDebug("SMTC 会话焦点改变：{SourceAppUserModelId}", sender?.ControlSession?.SourceAppUserModelId);
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
        private async void MediaManager_OnAnyPlaybackStateChanged(MediaSession sender, GlobalSystemMediaTransportControlsSessionPlaybackInfo args)
        {
            Logger.LogDebug("SMTC 播放状态改变：{SenderId} is now {PlaybackStatus}", sender.Id, args.PlaybackStatus);
            try
            {
                if (args.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)
                {
                    Dispatcher.UIThread.Invoke(() =>
                    {
                        MediaGrid.IsVisible = !Settings.IsHideWhenPaused;
                    });
                }

                await Dispatcher.UIThread.InvokeAsync(async () => await RefreshPlaybackInfo(sender));
            }
            catch
            {
                // ignored
            }
        }

        /// <summary>
        /// SMTC 媒体属性改变事件
        /// </summary>
        /// <param name="sender">发出事件的 SMTC 会话</param>
        /// <param name="args">事件参数</param>
        // ReSharper disable once AsyncVoidMethod
        private async void MediaManager_OnAnyMediaPropertyChanged(MediaSession sender, GlobalSystemMediaTransportControlsSessionMediaProperties args)
        {
            Logger.LogDebug("SMTC 媒体属性改变：{SenderId} is now playing {Title} {Artist}", sender.Id, args.Title, string.IsNullOrEmpty(args.Artist) ? "" : $"by {args.Artist}");
            try
            {
                await Dispatcher.UIThread.InvokeAsync(async () => await RefreshMediaProperties(sender));
            }
            catch
            {
                // ignored
            }
        }

        /// <summary>
        /// SMTC 时间属性改变事件
        /// </summary>
        /// <param name="sender">发出事件的 SMTC 会话</param>
        /// <param name="args">事件参数</param>
        private async void MediaManager_OnAnyTimelinePropertyChanged(MediaSession sender, GlobalSystemMediaTransportControlsSessionTimelineProperties args)
        {
            //Logger.LogDebug($"SMTC 时间属性改变：{sender.Id} timeline is now {args.Position}/{args.EndTime}");
            try
            {
                await Dispatcher.UIThread.InvokeAsync(async () => await RefreshTimelineProperties(sender));
            }
            catch
            {
                // ignored
            }
        }
    }
}