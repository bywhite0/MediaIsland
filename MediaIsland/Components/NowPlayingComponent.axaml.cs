using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Shared.Helpers;
using MediaIsland.Converters;
using MediaIsland.Helpers;
using MediaIsland.Models;
using MediaIsland.Services.Media;
using MediaIsland.Services.Media.SourceDisplay;
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
        private readonly IMediaSourceDisplayService _mediaSourceDisplayService;
        private readonly DispatcherTimer _timelineTimer;
        private ILogger<NowPlayingComponent> Logger { get; }

        private PluginSettings globalSettings;
        private DateTime _lastTimelineUpdate;
        private TimeSpan _basePosition;
        private TimeSpan _currentEndTime;
        private bool _isPlaying;
        private bool _isLoaded;
        private MediaInfo? _currentMediaInfo;
        private IDisposable? _sourceIconRadiusBinding;

        public NowPlayingComponent(
            ILogger<NowPlayingComponent> logger,
            IMediaService mediaService,
            IMediaSourceDisplayService mediaSourceDisplayService)
        {
            InitializeComponent();
            Logger = logger;
            _mediaService = mediaService;
            _mediaSourceDisplayService = mediaSourceDisplayService;
            globalSettings = Plugin.Instance?.Settings
                             ?? ConfigureFileHelper.LoadConfig<PluginSettings>(Path.Combine(Plugin.globalConfigFolder!, "Settings.json"));
            _timelineTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _timelineTimer.Tick += OnTimelineTimerTick;
        }

        private void NowPlayingComponent_OnLoaded(object? sender, RoutedEventArgs routedEventArgs)
        {
            _isLoaded = true;
            globalSettings = Plugin.Instance?.Settings ?? globalSettings;
            globalSettings.MediaSourceSettingsSaved -= GlobalSettings_OnMediaSourceSettingsSaved;
            globalSettings.MediaSourceSettingsSaved += GlobalSettings_OnMediaSourceSettingsSaved;
            globalSettings.PropertyChanged -= GlobalSettings_OnPropertyChanged;
            globalSettings.PropertyChanged += GlobalSettings_OnPropertyChanged;
            BindSourceIconRadius();
            Settings.PropertyChanged += OnSettingsPropertyChanged;
            UpdateMargin();
            ApplyProgressBarSideMargin();
            ApplyProgressBarColor(AlbumArt.Source as Bitmap);
            UpdateProgressBarVisibility(_currentEndTime);
            LoadCurrentPlayingInfoAsync();
        }

        private void NowPlayingComponent_OnUnloaded(object? sender, RoutedEventArgs routedEventArgs)
        {
            _isLoaded = false;
            _timelineTimer.Stop();
            _sourceIconRadiusBinding?.Dispose();
            _sourceIconRadiusBinding = null;
            globalSettings.MediaSourceSettingsSaved -= GlobalSettings_OnMediaSourceSettingsSaved;
            globalSettings.PropertyChanged -= GlobalSettings_OnPropertyChanged;
            Settings.PropertyChanged -= OnSettingsPropertyChanged;
            _mediaService.MediaInfoChanged -= MediaService_OnMediaInfoChanged;
        }

        private void GlobalSettings_OnMediaSourceSettingsSaved(object? sender, EventArgs e)
        {
            if (!_isLoaded)
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (_mediaService.CurrentMediaInfo is { } mediaInfo)
                {
                    _ = RefreshMediaInfo(mediaInfo);
                }
                else
                {
                    _ = HideMediaGridAsync();
                }
            });
        }

        private void GlobalSettings_OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is not nameof(PluginSettings.ProgressBarColorMode))
            {
                return;
            }

            if (!_isLoaded)
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                ApplyProgressBarColor(AlbumArt.Source as Bitmap);
            });
        }

        private void BindSourceIconRadius()
        {
            _sourceIconRadiusBinding?.Dispose();
            _sourceIconRadiusBinding = SourceIconBorder.Bind(
                Border.CornerRadiusProperty,
                new Binding(nameof(NowPlayingComponentConfig.SourceIconRadius))
                {
                    Source = Settings,
                    Converter = new DoubleToCornerRadiusConverter()
                });
        }

        private void UpdateMargin()
        {
            Dispatcher.UIThread.Post(() =>
            {
                MarginHost.Margin = new Thickness(
                    Settings.IsLeftNegativeMargin ? -12 : 0,
                    0,
                    Settings.IsRightNegativeMargin ? -12 : 0,
                    0);
            });
        }

        private void OnSettingsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(NowPlayingComponentConfig.IsLeftNegativeMargin):
                case nameof(NowPlayingComponentConfig.IsRightNegativeMargin):
                    UpdateMargin();
                    break;
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
                case "IsShowSource":
                    Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        SourceIconBorder.IsVisible = Settings.IsShowSource && SourceIcon.Source != null;
                    });
                    break;
                case nameof(NowPlayingComponentConfig.IsShowProgressBar):
                    Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        UpdateProgressBarVisibility(_currentEndTime);
                    });
                    break;
                case nameof(NowPlayingComponentConfig.IsProgressBarLeftMargin):
                case nameof(NowPlayingComponentConfig.IsProgressBarRightMargin):
                    Dispatcher.UIThread.InvokeAsync(ApplyProgressBarSideMargin);
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

            if (!MediaSourceFilter.IsEnabled(e.MediaInfo.SourceApp, globalSettings.MediaSourceList))
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
                if (MediaSourceFilter.IsEnabled(mediaInfo.SourceApp, globalSettings.MediaSourceList))
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
            var thumbnail = mediaInfo.Thumbnail;
            var sourceDisplayInfo = await ResolveSourceDisplayInfoAsync(mediaInfo.SourceApp);
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
                SourceText.Text = sourceDisplayInfo.DisplayName;
                SourceIcon.Source = sourceDisplayInfo.Icon;
                SourceIconBorder.IsVisible = Settings.IsShowSource && sourceDisplayInfo.Icon != null;

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

                ApplyProgressBarColor(thumbnail);
            });
        }

        private async Task RefreshPlaybackInfo(MediaInfo mediaInfo)
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
                        if (HasTimeline(_currentEndTime))
                        {
                            _timelineTimer.Start();
                        }
                        else
                        {
                            _timelineTimer.Stop();
                        }

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

        private Task<MediaSourceDisplayInfo> ResolveSourceDisplayInfoAsync(string sourceApp)
        {
            return _mediaSourceDisplayService.ResolveAsync(sourceApp);
        }

        private async Task RefreshTimelineProperties(MediaInfo mediaInfo)
        {
            if (!_isLoaded)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _currentEndTime = mediaInfo.Duration > TimeSpan.Zero
                    ? mediaInfo.Duration
                    : TimeSpan.Zero;
                _basePosition = ClampPosition(mediaInfo.Position, _currentEndTime);
                _lastTimelineUpdate = DateTime.Now;
                UpdateTimelineUi(_basePosition, _currentEndTime);
                if (mediaInfo.PlaybackInfo.PlaybackState == MediaPlaybackState.Playing && HasTimeline(_currentEndTime))
                {
                    _isPlaying = true;
                    _timelineTimer.Start();
                }
                else if (!HasTimeline(_currentEndTime))
                {
                    _timelineTimer.Stop();
                }
            });
        }

        private void OnTimelineTimerTick(object? sender, EventArgs e)
        {
            if (!_isPlaying) return;
            if (!HasTimeline(_currentEndTime))
            {
                _timelineTimer.Stop();
                return;
            }

            var elapsed = DateTime.Now - _lastTimelineUpdate;
            var currentPosition = ClampPosition(_basePosition + elapsed, _currentEndTime);
            if (currentPosition >= _currentEndTime)
            {
                _timelineTimer.Stop();
            }

            UpdateTimelineUi(currentPosition, _currentEndTime);
        }

        private void UpdateTimelineUi(TimeSpan position, TimeSpan duration)
        {
            if (Settings.SubInfoType == 1)
            {
                if (HasTimeline(duration) && position < duration)
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
            TimeText.Text = (duration.Hours == 0) ? $@"{position:mm\:ss} / {duration:mm\:ss}" : $@"{(int)position.TotalHours:00}:{position:mm\:ss} / {(int)duration.TotalHours:00}:{duration:mm\:ss}";

            UpdateProgressBar(position, duration);
        }

        private static bool HasTimeline(TimeSpan duration)
        {
            return duration > TimeSpan.Zero;
        }

        private static TimeSpan ClampPosition(TimeSpan position, TimeSpan duration)
        {
            if (position < TimeSpan.Zero)
            {
                return TimeSpan.Zero;
            }

            if (HasTimeline(duration) && position > duration)
            {
                return duration;
            }

            return position;
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
                ProgressBar.Value = 0;
                ProgressContainer.IsVisible = false;
            });
        }

        private void UpdateProgressBar(TimeSpan position, TimeSpan duration)
        {
            UpdateProgressBarVisibility(duration);
            if (!HasTimeline(duration))
            {
                ProgressBar.Value = 0;
                return;
            }

            var ratio = position.TotalSeconds / duration.TotalSeconds;
            ProgressBar.Value = Math.Clamp(ratio, 0.0, 1.0);
        }


        private const double ProgressBarSideMargin = 12;

        private void ApplyProgressBarSideMargin()
        {
            ProgressContainer.Margin = new Thickness(
                Settings.IsProgressBarLeftMargin ? ProgressBarSideMargin : 0,
                0,
                Settings.IsProgressBarRightMargin ? ProgressBarSideMargin : 0,
                0);
        }

        private void UpdateProgressBarVisibility(TimeSpan duration)
        {
            ProgressContainer.IsVisible = Settings.IsShowProgressBar && HasTimeline(duration);
        }

        private void ApplyProgressBarColor(Bitmap? thumbnail)
        {
            if (globalSettings.ProgressBarColorMode == (int)MediaIsland.Models.ProgressBarColorMode.CoverTheme)
            {
                var color = CoverThemeColorHelper.TryExtract(thumbnail);
                if (color is { } coverColor)
                {
                    ProgressBar.Foreground = new SolidColorBrush(coverColor);
                    return;
                }
            }

            // ClassIsland 主题色：清除本地值，沿用主题 ProgressBar 前景（Accent）
            ProgressBar.ClearValue(ProgressBar.ForegroundProperty);
        }

    }
}
