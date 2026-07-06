using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Shared.Helpers;
using MaterialDesignThemes.Wpf;
using MediaIsland.Helpers;
using MediaIsland.Models;
using MediaIsland.Services;
using Microsoft.Extensions.Logging;
using Windows.Media.Control;

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
        private readonly IMediaService _mediaService;
        private readonly DispatcherTimer _timelineTimer;
        private readonly PluginSettings globalSettings;

        private ILogger<NowPlayingComponent> Logger { get; }

        private TimeSpan _basePosition;
        private TimeSpan _currentEndTime;
        private long _lastTimelineUpdate = Environment.TickCount64;
        private bool _isPlaying;
        private double _playbackRate = 1.0;

        public NowPlayingComponent(IMediaService mediaService, ILogger<NowPlayingComponent> logger)
        {
            InitializeComponent();
            _mediaService = mediaService;
            Logger = logger;
            globalSettings =
                ConfigureFileHelper.LoadConfig<PluginSettings>(
                    Path.Combine(Plugin.globalConfigFolder!, "Settings.json"));
            _timelineTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _timelineTimer.Tick += OnTimelineTimerTick;
        }

        private async void NowPlayingComponent_OnLoaded(object sender, RoutedEventArgs e)
        {
            Settings.PropertyChanged += OnSettingsPropertyChanged;
            _mediaService.OnMediaPropertiesChanged += MediaService_OnMediaPropertiesChanged;
            _mediaService.OnPlaybackStateChanged += MediaService_OnPlaybackStateChanged;
            _mediaService.OnTimelinePropertyChanged += MediaService_OnTimelinePropertyChanged;
            _mediaService.OnFocusedSessionChanged += MediaService_OnFocusedSessionChanged;

            await LoadCurrentPlayingInfoAsync();
        }

        private void NowPlayingComponent_OnUnloaded(object sender, RoutedEventArgs e)
        {
            _timelineTimer.Stop();
            Settings.PropertyChanged -= OnSettingsPropertyChanged;
            _mediaService.OnMediaPropertiesChanged -= MediaService_OnMediaPropertiesChanged;
            _mediaService.OnPlaybackStateChanged -= MediaService_OnPlaybackStateChanged;
            _mediaService.OnTimelinePropertyChanged -= MediaService_OnTimelinePropertyChanged;
            _mediaService.OnFocusedSessionChanged -= MediaService_OnFocusedSessionChanged;
        }

        private async void OnSettingsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Settings.IsHideWhenPaused))
            {
                var playbackStatus = _mediaService.CurrentMediaInfo?.PlaybackInfo?.PlaybackStatus;
                if (playbackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        MediaGrid.Visibility =
                            Settings.IsHideWhenPaused ? Visibility.Collapsed : Visibility.Visible;
                    });
                }
            }

            if (e.PropertyName == nameof(Settings.SubInfoType))
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (Settings.SubInfoType == 0)
                    {
                        artistText.Visibility = Visibility.Visible;
                        timeText.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        UpdateTimelineUi(GetCurrentTimelinePosition(), _currentEndTime);
                    }
                });
            }
        }

        /// <summary>
        /// 获取 SMTC 信息并更新 UI
        /// </summary>
        private async Task LoadCurrentPlayingInfoAsync()
        {
            await _mediaService.StartAsync();
            if (_mediaService.CurrentMediaInfo != null)
            {
                await RefreshMediaInfo(_mediaService.CurrentMediaInfo);
                return;
            }

            Logger.LogInformation("不存在 SMTC 会话信息，隐藏组件 UI");
            await Dispatcher.InvokeAsync(ClearMediaInfo);
        }

        /// <summary>
        /// 使用获取到的 SMTC 信息刷新 UI
        /// </summary>
        private async Task RefreshMediaInfo(MediaInfo? info)
        {
            try
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!IsLoaded || Settings == null)
                    {
                        return;
                    }

                    if (info == null)
                    {
                        ClearMediaInfo();
                        return;
                    }

                    if (!IsSourceEnabled(info.SourceApp, globalSettings.MediaSourceList))
                    {
                        Logger.LogInformation("当前 SMTC 会话 [{SourceApp}] 已禁用，自动隐藏", info.SourceApp);
                        ClearMediaInfo();
                        return;
                    }

                    Logger.LogTrace(
                        "当前 SMTC 信息：[{SourceApp}] {Artist} - {Title} ({PlaybackStatus}) [{Position} / {Duration}]",
                        info.SourceApp,
                        info.Artist,
                        info.Title,
                        info.PlaybackInfo?.PlaybackStatus,
                        info.Position,
                        info.Duration);

                    MediaGrid.Visibility = Visibility.Visible;
                    titleText.Text = info.Title;
                    artistText.Text = info.Artist;

                    if (info.Thumbnail != null)
                    {
                        AlbumArt.ImageSource = info.Thumbnail;
                        CoverPlaceholder.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        AlbumArt.ImageSource = null;
                        CoverPlaceholder.Visibility = Visibility.Visible;
                    }

                    (string appName, ImageSource? appIcon) =
                        MediaPlayerData.GetMediaPlayerData(info.SourceApp);
                    sourceText.Text = appName;
                    sourceIcon.ImageSource = appIcon;

                    UpdatePlaybackInfo(info.PlaybackInfo);
                    UpdateTimelineState(info.Position, info.Duration, info.PlaybackInfo);
                    UpdateTimelineUi(_basePosition, _currentEndTime);
                });
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to refresh now playing UI.");
            }
        }

        private async void MediaService_OnMediaPropertiesChanged(object? sender, MediaInfo? info)
        {
            await RefreshMediaInfo(info);
        }

        private void MediaService_OnPlaybackStateChanged(
            object? sender,
            GlobalSystemMediaTransportControlsSessionPlaybackInfo playbackInfo)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (!IsLoaded)
                {
                    return;
                }

                UpdatePlaybackInfo(playbackInfo);
            });
        }

        private void MediaService_OnTimelinePropertyChanged(
            object? sender,
            GlobalSystemMediaTransportControlsSessionTimelineProperties timeline)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (!IsLoaded)
                {
                    return;
                }

                UpdateTimelineState(timeline.Position, timeline.EndTime, _mediaService.CurrentMediaInfo?.PlaybackInfo);
                UpdateTimelineUi(_basePosition, _currentEndTime);
            });
        }

        private async void MediaService_OnFocusedSessionChanged(object? sender, EventArgs e)
        {
            if (_mediaService.CurrentMediaInfo == null)
            {
                await Dispatcher.InvokeAsync(ClearMediaInfo);
                return;
            }

            await RefreshMediaInfo(_mediaService.CurrentMediaInfo);
        }

        private void UpdatePlaybackInfo(GlobalSystemMediaTransportControlsSessionPlaybackInfo? playbackInfo)
        {
            if (playbackInfo == null)
            {
                _isPlaying = false;
                _timelineTimer.Stop();
                StatusIcon.Kind = PackIconKind.Stop;
                return;
            }

            _isPlaying = playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            _playbackRate = playbackInfo.PlaybackRate ?? 1.0;

            switch (playbackInfo.PlaybackStatus)
            {
                case GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing:
                    _lastTimelineUpdate = Environment.TickCount64;
                    _timelineTimer.Start();
                    MediaGrid.Visibility = Visibility.Visible;
                    StatusIcon.Kind = PackIconKind.Play;
                    break;
                case GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused:
                    _timelineTimer.Stop();
                    MediaGrid.Visibility = Settings.IsHideWhenPaused ? Visibility.Collapsed : Visibility.Visible;
                    StatusIcon.Kind = PackIconKind.Pause;
                    break;
                case GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped:
                    _timelineTimer.Stop();
                    MediaGrid.Visibility = Visibility.Visible;
                    StatusIcon.Kind = PackIconKind.Stop;
                    break;
                case GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing:
                    MediaGrid.Visibility = Visibility.Visible;
                    StatusIcon.Kind = PackIconKind.Refresh;
                    break;
            }
        }

        private void UpdateTimelineState(
            TimeSpan position,
            TimeSpan duration,
            GlobalSystemMediaTransportControlsSessionPlaybackInfo? playbackInfo)
        {
            _basePosition = ClampPosition(position, duration);
            _currentEndTime = duration;
            _lastTimelineUpdate = Environment.TickCount64;

            if (playbackInfo != null)
            {
                _isPlaying =
                    playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                _playbackRate = playbackInfo.PlaybackRate ?? 1.0;
                if (_isPlaying)
                {
                    _timelineTimer.Start();
                }
                else
                {
                    _timelineTimer.Stop();
                }
            }
        }

        private void OnTimelineTimerTick(object? sender, EventArgs e)
        {
            if (!_isPlaying)
            {
                return;
            }

            var currentPosition = GetCurrentTimelinePosition();
            if (_currentEndTime > TimeSpan.Zero && currentPosition >= _currentEndTime)
            {
                currentPosition = _currentEndTime;
                _timelineTimer.Stop();
            }

            UpdateTimelineUi(currentPosition, _currentEndTime);
        }

        private TimeSpan GetCurrentTimelinePosition()
        {
            if (!_isPlaying)
            {
                return _basePosition;
            }

            var elapsed = Environment.TickCount64 - _lastTimelineUpdate;
            return ClampPosition(
                _basePosition + TimeSpan.FromMilliseconds(elapsed * _playbackRate),
                _currentEndTime);
        }

        private static TimeSpan ClampPosition(TimeSpan position, TimeSpan duration)
        {
            if (position < TimeSpan.Zero)
            {
                return TimeSpan.Zero;
            }

            if (duration > TimeSpan.Zero && position > duration)
            {
                return duration;
            }

            return position;
        }

        private void UpdateTimelineUi(TimeSpan position, TimeSpan duration)
        {
            timeText.Text = $"{FormatTime(position)} / {FormatTime(duration)}";

            if (Settings.SubInfoType != 1)
            {
                return;
            }

            if (duration > TimeSpan.Zero && position < duration)
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

        private static string FormatTime(TimeSpan value)
        {
            return value.TotalHours >= 1
                ? $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}"
                : $"{value.Minutes:00}:{value.Seconds:00}";
        }

        private void ClearMediaInfo()
        {
            _timelineTimer.Stop();
            _isPlaying = false;
            _basePosition = TimeSpan.Zero;
            _currentEndTime = TimeSpan.Zero;
            timeText.Text = "00:00 / 00:00";
            MediaGrid.Visibility = Visibility.Collapsed;
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
