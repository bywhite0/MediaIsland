using System.IO;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Shared.Helpers;
using MediaIsland.Helpers;
using MediaIsland.Models;
using MediaIsland.Services.Media;
using Microsoft.Extensions.Logging;
using RoutedEventArgs = Avalonia.Interactivity.RoutedEventArgs;

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
        private readonly IMediaService _mediaService;
        private readonly DispatcherTimer _timelineTimer;
        private ILogger<NowPlayingComponent> Logger { get; }

        private PluginSettings globalSettings;
        private DateTime _lastTimelineUpdate;
        private TimeSpan _basePosition;
        private TimeSpan _currentEndTime;
        private bool _isPlaying;
        private bool _isLoaded;
        private MediaInfo? _currentMediaInfo;

        public NowPlayingComponent(ILogger<NowPlayingComponent> logger, IMediaService mediaService)
        {
            InitializeComponent();
            Logger = logger;
            _mediaService = mediaService;
            globalSettings = ConfigureFileHelper.LoadConfig<PluginSettings>(Path.Combine(Plugin.globalConfigFolder!, "Settings.json"));
            _timelineTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _timelineTimer.Tick += OnTimelineTimerTick;
        }

        private void NowPlayingComponent_OnLoaded(object? sender, RoutedEventArgs routedEventArgs)
        {
            _isLoaded = true;
            Settings.PropertyChanged += OnSettingsPropertyChanged;
            LoadCurrentPlayingInfoAsync();
        }

        private void NowPlayingComponent_OnUnloaded(object? sender, RoutedEventArgs routedEventArgs)
        {
            _isLoaded = false;
            _timelineTimer.Stop();
            Settings.PropertyChanged -= OnSettingsPropertyChanged;
            _mediaService.MediaInfoChanged -= MediaService_OnMediaInfoChanged;
        }

        private void OnSettingsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case "IsHideWhenPaused":
                    if (_currentMediaInfo?.PlaybackInfo.PlaybackState == MediaPlaybackState.Paused)
                    {
                        Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            MediaGrid.IsVisible = !Settings.IsHideWhenPaused;
                        });
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
        /// 获取媒体服务信息并更新 UI
        /// </summary>
        // ReSharper disable once AsyncVoidMethod
        private async void LoadCurrentPlayingInfoAsync()
        {
            _mediaService.MediaInfoChanged -= MediaService_OnMediaInfoChanged;
            _mediaService.MediaInfoChanged += MediaService_OnMediaInfoChanged;

            try
            {
                await _mediaService.EnsureStartedAsync();
                Logger.LogInformation("尝试获取媒体会话信息");
                if (_mediaService.CurrentMediaInfo != null)
                {
                    Logger.LogInformation("存在媒体会话信息");
                    Logger.LogDebug("刷新【正在播放】组件内容");
                    await RefreshMediaInfo(_mediaService.CurrentMediaInfo);
                }
                else
                {
                    Logger.LogInformation("不存在媒体会话信息，隐藏组件 UI");
                    await HideMediaGridAsync();
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("获取媒体会话时发生错误: {ExMessage}", ex.Message);
                await HideMediaGridAsync();
            }
        }

        // ReSharper disable once AsyncVoidMethod
        private async void MediaService_OnMediaInfoChanged(object? sender, MediaInfoChangedEventArgs e)
        {
            if (!_isLoaded)
            {
                return;
            }

            _currentMediaInfo = e.MediaInfo;
            if (e.MediaInfo == null)
            {
                await HideMediaGridAsync();
                return;
            }

            if (!IsSourceEnabled(e.MediaInfo.SourceApp, globalSettings.MediaSourceList))
            {
                Logger.LogInformation("当前媒体会话 [{SourceApp}] 已禁用，自动隐藏", e.MediaInfo.SourceApp);
                await HideMediaGridAsync();
                return;
            }

            switch (e.ChangeKind)
            {
                case MediaInfoChangeKind.Playback:
                    await RefreshPlaybackInfo(e.MediaInfo);
                    await RefreshTimelineProperties(e.MediaInfo);
                    break;
                case MediaInfoChangeKind.Timeline:
                    await RefreshTimelineProperties(e.MediaInfo);
                    break;
                case MediaInfoChangeKind.CurrentSession:
                case MediaInfoChangeKind.MediaProperties:
                default:
                    await RefreshMediaInfo(e.MediaInfo);
                    break;
            }
        }

        /// <summary>
        /// 使用获取到的媒体信息刷新 UI
        /// </summary>
        private async Task RefreshMediaInfo(MediaInfo mediaInfo)
        {
            _currentMediaInfo = mediaInfo;
            if (!_isLoaded)
            {
                return;
            }

            try
            {
                if (IsSourceEnabled(mediaInfo.SourceApp, globalSettings.MediaSourceList))
                {
                    Logger.LogTrace(
                        "当前媒体信息：[{SourceApp}] {Artist} - {Title} ({PlaybackStatus}) [{TimelinePosition} / {TimelineEndTime}]",
                        mediaInfo.SourceApp,
                        mediaInfo.Artist,
                        mediaInfo.Title,
                        mediaInfo.PlaybackInfo.PlaybackState,
                        mediaInfo.Position,
                        mediaInfo.Duration);

                    await RefreshMediaProperties(mediaInfo);
                    await RefreshPlaybackInfo(mediaInfo);
                    await RefreshTimelineProperties(mediaInfo);
                }
                else
                {
                    Logger.LogInformation("当前媒体会话 [{SourceApp}] 已禁用，自动隐藏", mediaInfo.SourceApp);
                    await HideMediaGridAsync();
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("获取媒体信息失败：{ExMessage}", ex.Message);
            }
        }

        private async Task RefreshMediaProperties(MediaInfo mediaInfo)
        {
            try
            {
                var thumbnail = mediaInfo.Thumbnail;
                if (mediaInfo.ThumbnailSource != null &&
                    AppInfoHelper.IsSourceAppSpotify(mediaInfo.SourceApp) &&
                    globalSettings.IsCutSpotifyTrademarkEnabled)
                {
                    thumbnail = await mediaInfo.ThumbnailSource.LoadBitmapAsync(true, CancellationToken.None);
                }

                if (!_isLoaded)
                {
                    return;
                }

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    TitleText.Text = mediaInfo.Title ?? "未知标题";
                    ArtistText.Text = mediaInfo.Artist ?? "未知艺术家";

                    if (thumbnail != null)
                    {
                        AlbumArt.Source = thumbnail;
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

        private async Task RefreshPlaybackInfo(MediaInfo mediaInfo)
        {
            try
            {
                if (!_isLoaded)
                {
                    return;
                }

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    switch (mediaInfo.PlaybackInfo.PlaybackState)
                    {
                        case MediaPlaybackState.Playing:
                            _isPlaying = true;
                            _lastTimelineUpdate = DateTime.Now;
                            _timelineTimer.Start();
                            MediaGrid.IsVisible = true;
                            StatusIcon.Glyph = "\uEDB8";
                            break;
                        case MediaPlaybackState.Paused:
                            _isPlaying = false;
                            _timelineTimer.Stop();
                            StatusIcon.Glyph = "\uEC90";
                            MediaGrid.IsVisible = !Settings.IsHideWhenPaused;
                            break;
                        case MediaPlaybackState.Stopped:
                            _isPlaying = false;
                            _timelineTimer.Stop();
                            StatusIcon.Glyph = "\uF086";
                            break;
                        case MediaPlaybackState.Changing:
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

        private async Task RefreshTimelineProperties(MediaInfo mediaInfo)
        {
            try
            {
                if (!_isLoaded)
                {
                    return;
                }

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _basePosition = mediaInfo.Position;
                    _currentEndTime = mediaInfo.Duration;
                    _lastTimelineUpdate = DateTime.Now;
                    UpdateTimelineUi(_basePosition, _currentEndTime);
                });
            }
            catch (Exception ex)
            {
                Logger.LogWarning("无法获取时间轴信息：{ExMessage}", ex.Message);
            }
        }

        private void OnTimelineTimerTick(object? sender, EventArgs e)
        {
            if (!_isPlaying) return;
            var elapsed = DateTime.Now - _lastTimelineUpdate;
            var currentPosition = _basePosition + elapsed;
            if (currentPosition > _currentEndTime) currentPosition = _currentEndTime;
            UpdateTimelineUi(currentPosition, _currentEndTime);
        }

        private void UpdateTimelineUi(TimeSpan position, TimeSpan duration)
        {
            if (Settings.SubInfoType == 1)
            {
                if (position != duration)
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
            TimeText.Text = (duration.Hours == 0) ? $@"{position:mm\:ss} / {duration:mm\:ss}" : $@"{position} / {duration}";
        }

        private async Task HideMediaGridAsync()
        {
            _isPlaying = false;
            _timelineTimer.Stop();
            if (!_isLoaded)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                MediaGrid.IsVisible = false;
            });
        }

        private static bool IsSourceEnabled(string appUserModelId, IEnumerable<MediaSource> sources)
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
