using System.IO;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Shared.Helpers;
using Lyricify.Lyrics.Models;
using MaterialDesignThemes.Wpf;
using MediaIsland.Models;
using MediaIsland.Services;
using Microsoft.Extensions.Logging;
using Windows.Media.Control;

namespace MediaIsland.Components;

[ComponentInfo(
    "A681FD00-04F7-4E7B-9236-FAC85780D518",
    "实时歌词",
    PackIconKind.MusicNote,
    "根据当前播放媒体搜索并显示同步歌词。"
)]
public partial class LyricsComponent : ComponentBase<LyricsComponentConfig>
{
    private static readonly Duration LyricsTransitionDuration = TimeSpan.FromMilliseconds(220);
    private static readonly IEasingFunction LyricsTransitionEasing = new CubicEase
    {
        EasingMode = EasingMode.EaseOut
    };

    private readonly IMediaService _mediaService;
    private readonly ILogger<LyricsComponent> _logger;
    private readonly LyricsSearchService _lyricsSearchService;
    private readonly DispatcherTimer _lyricsTimer;
    private readonly object _syncLock = new();
    private readonly PluginSettings _globalSettings;

    private LyricsData? _currentLyrics;
    private string? _currentLine;
    private string? _lastTitle;
    private string? _lastArtist;
    private string _lastStatusText = string.Empty;
    private bool _isCurrentTextStatus = true;
    private TimeSpan _lastSmtcPosition;
    private long _lastSmtcUpdateTime = Environment.TickCount64;
    private bool _isPlaying;
    private double _playbackRate = 1.0;
    private GlobalSystemMediaTransportControlsSessionPlaybackStatus? _playbackStatus;
    private CancellationTokenSource? _searchCts;
    private int _searchVersion;

    public LyricsComponent(IMediaService mediaService, ILogger<LyricsComponent> logger)
    {
        InitializeComponent();
        _mediaService = mediaService;
        _logger = logger;
        _lyricsSearchService = new LyricsSearchService(logger);
        _globalSettings = Plugin.globalConfigFolder is null
            ? new PluginSettings()
            : ConfigureFileHelper.LoadConfig<PluginSettings>(
                Path.Combine(Plugin.globalConfigFolder, "Settings.json"));
        _lyricsTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(80)
        };
        _lyricsTimer.Tick += LyricsTimer_OnTick;
    }

    private async void LyricsComponent_OnLoaded(object sender, RoutedEventArgs e)
    {
        _mediaService.OnMediaPropertiesChanged += MediaService_OnMediaPropertiesChanged;
        _mediaService.OnTimelinePropertyChanged += MediaService_OnTimelinePropertyChanged;
        _mediaService.OnPlaybackStateChanged += MediaService_OnPlaybackStateChanged;
        _mediaService.OnFocusedSessionChanged += MediaService_OnFocusedSessionChanged;
        Settings.PropertyChanged += Settings_OnPropertyChanged;

        _lyricsTimer.Start();
        SetStatus("等待媒体信息...");
        await _mediaService.StartAsync();
        if (_mediaService.CurrentMediaInfo != null)
        {
            await HandleMediaInfoAsync(_mediaService.CurrentMediaInfo);
        }
    }

    private void LyricsComponent_OnUnloaded(object sender, RoutedEventArgs e)
    {
        _lyricsTimer.Stop();
        _mediaService.OnMediaPropertiesChanged -= MediaService_OnMediaPropertiesChanged;
        _mediaService.OnTimelinePropertyChanged -= MediaService_OnTimelinePropertyChanged;
        _mediaService.OnPlaybackStateChanged -= MediaService_OnPlaybackStateChanged;
        _mediaService.OnFocusedSessionChanged -= MediaService_OnFocusedSessionChanged;
        Settings.PropertyChanged -= Settings_OnPropertyChanged;
        CancelCurrentSearch();
    }

    private void Settings_OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LyricsComponentConfig.IsShowStatusText))
        {
            ApplyCurrentDisplaySettings();
            return;
        }

        if (e.PropertyName is nameof(LyricsComponentConfig.IsHideWhenEmpty) or nameof(LyricsComponentConfig.IsHideWhenPaused))
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (!IsLoaded || Settings == null)
                {
                    return;
                }

                UpdateEmptyVisibility();
            });
        }
    }

    private async void MediaService_OnMediaPropertiesChanged(object? sender, MediaInfo? info)
    {
        await HandleMediaInfoAsync(info);
    }

    private void MediaService_OnTimelinePropertyChanged(object? sender, GlobalSystemMediaTransportControlsSessionTimelineProperties timeline)
    {
        if (!IsCurrentSourceEnabled())
        {
            return;
        }

        lock (_syncLock)
        {
            _lastSmtcPosition = timeline.Position;
            _lastSmtcUpdateTime = Environment.TickCount64;
        }
    }

    private void MediaService_OnPlaybackStateChanged(object? sender, GlobalSystemMediaTransportControlsSessionPlaybackInfo playbackInfo)
    {
        if (!IsCurrentSourceEnabled())
        {
            return;
        }

        lock (_syncLock)
        {
            _isPlaying = playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            _playbackRate = playbackInfo.PlaybackRate ?? 1.0;
            _playbackStatus = playbackInfo.PlaybackStatus;
        }

        Dispatcher.InvokeAsync(() =>
        {
            if (!IsLoaded || Settings == null)
            {
                return;
            }

            UpdateEmptyVisibility();
        });
        _logger.LogInformation("[Lyrics] Playback status: {Status}", playbackInfo.PlaybackStatus);
    }

    private void MediaService_OnFocusedSessionChanged(object? sender, EventArgs e)
    {
        if (_mediaService.CurrentMediaInfo == null)
        {
            ClearLyrics("没有可用的媒体会话");
            return;
        }

        if (!IsSourceEnabled(_mediaService.CurrentMediaInfo.SourceApp))
        {
            ClearLyrics("当前播放源已禁用");
        }
    }

    private async Task HandleMediaInfoAsync(MediaInfo? info)
    {
        try
        {
            if (info == null)
            {
                ClearLyrics("没有可用的媒体会话");
                return;
            }

            if (!IsSourceEnabled(info.SourceApp))
            {
                _logger.LogInformation("[Lyrics] Source {SourceApp} is disabled, hiding lyrics.", info.SourceApp);
                ClearLyrics("当前播放源已禁用");
                return;
            }

            lock (_syncLock)
            {
                _lastSmtcPosition = info.Position;
                _lastSmtcUpdateTime = Environment.TickCount64;
                _isPlaying = info.PlaybackInfo?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                _playbackRate = info.PlaybackInfo?.PlaybackRate ?? 1.0;
                _playbackStatus = info.PlaybackInfo?.PlaybackStatus;
            }

            await Dispatcher.InvokeAsync(() =>
            {
                if (!IsLoaded || Settings == null)
                {
                    return;
                }

                UpdateEmptyVisibility();
            });

            if (info.Title == _lastTitle && info.Artist == _lastArtist)
            {
                return;
            }

            _lastTitle = info.Title;
            _lastArtist = info.Artist;
            var searchCts = new CancellationTokenSource();
            var previousSearchCts = Interlocked.Exchange(ref _searchCts, searchCts);
            CancelSearch(previousSearchCts);
            var token = searchCts.Token;
            var version = Interlocked.Increment(ref _searchVersion);

            try
            {
                lock (_syncLock)
                {
                    _currentLyrics = null;
                    _currentLine = null;
                }

                _logger.LogInformation(
                    "[Metadata] {Title} - {Artist} ({Album}), duration {Duration}",
                    info.Title,
                    info.Artist,
                    info.AlbumTitle,
                    info.Duration);
                _logger.LogInformation("[Source] {Source}", info.SourceApp);
                SetStatus($"正在查找歌词: {info.Title}");

                var result = await _lyricsSearchService.SearchAsync(info, token);
                token.ThrowIfCancellationRequested();
                if (version != _searchVersion || token.IsCancellationRequested)
                {
                    return;
                }

                var lyrics = result?.Lyrics;
                lock (_syncLock)
                {
                    _currentLyrics = lyrics;
                    _currentLine = null;
                }

                if (lyrics == null)
                {
                    SetStatus("未找到歌词");
                }
                else
                {
                    SetStatus(string.Empty);
                }
            }
            finally
            {
                Interlocked.CompareExchange(ref _searchCts, null, searchCts);
                searchCts.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Lyrics] Error while handling media info.");
            ClearLyrics("查找歌词失败");
        }
    }

    private void LyricsTimer_OnTick(object? sender, EventArgs e)
    {
        LyricsData? lyrics;
        double currentMs;

        lock (_syncLock)
        {
            lyrics = _currentLyrics;
            if (lyrics == null)
            {
                return;
            }

            currentMs = _lastSmtcPosition.TotalMilliseconds;
            if (_isPlaying)
            {
                var elapsed = Environment.TickCount64 - _lastSmtcUpdateTime;
                currentMs += elapsed * _playbackRate;
            }
        }

        var line = lyrics.Lines?.LastOrDefault(lineInfo => lineInfo.StartTime <= currentMs);

        if (line == null)
        {
            return;
        }

        lock (_syncLock)
        {
            if (line.Text == _currentLine)
            {
                return;
            }

            _currentLine = line.Text;
        }

        if (string.IsNullOrWhiteSpace(line.Text))
        {
            SetStatus(string.Empty);
            return;
        }

        _logger.LogInformation("[Lyrics] {Line}", line.Text);
        SetText(line.Text, isStatusText: false);
    }

    private void ClearLyrics(string status)
    {
        Interlocked.Increment(ref _searchVersion);
        CancelCurrentSearch();
        _lastTitle = null;
        _lastArtist = null;

        lock (_syncLock)
        {
            _currentLyrics = null;
            _currentLine = null;
            _lastSmtcPosition = TimeSpan.Zero;
            _lastSmtcUpdateTime = Environment.TickCount64;
            _isPlaying = false;
            _playbackRate = 1.0;
            _playbackStatus = null;
        }

        SetStatus(status);
    }

    private bool IsCurrentSourceEnabled()
    {
        var current = _mediaService.CurrentMediaInfo;
        return current == null || IsSourceEnabled(current.SourceApp);
    }

    private bool IsSourceEnabled(string appUserModelId)
    {
        foreach (var source in _globalSettings.MediaSourceList)
        {
            if (appUserModelId.Equals(source.Source, StringComparison.Ordinal))
            {
                return source.IsEnabled;
            }
        }

        return true;
    }

    private void SetStatus(string text)
    {
        _lastStatusText = text;
        Dispatcher.InvokeAsync(() =>
        {
            if (!IsLoaded || Settings == null)
            {
                return;
            }

            _isCurrentTextStatus = true;
            SetTextCore(GetVisibleText(text, isStatusText: true), isStatusText: true);
        });
    }

    private void SetText(string text, bool isStatusText)
    {
        if (isStatusText)
        {
            _lastStatusText = text;
        }

        Dispatcher.InvokeAsync(() =>
        {
            if (!IsLoaded || Settings == null)
            {
                return;
            }

            _isCurrentTextStatus = isStatusText;
            SetTextCore(GetVisibleText(text, isStatusText), isStatusText);
        });
    }

    private void ApplyCurrentDisplaySettings()
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (!IsLoaded || Settings == null)
            {
                return;
            }

            if (_isCurrentTextStatus)
            {
                SetTextCore(GetVisibleText(_lastStatusText, isStatusText: true), isStatusText: true);
                return;
            }

            UpdateEmptyVisibility();
        });
    }

    private string GetVisibleText(string text, bool isStatusText)
    {
        return isStatusText && !Settings.IsShowStatusText ? string.Empty : text;
    }

    private void SetTextCore(string text, bool isStatusText)
    {
        var targetOpacity = isStatusText ? 0.72 : 1.0;
        if (lyricsText.Text == text && Math.Abs(lyricsText.Opacity - targetOpacity) < 0.001)
        {
            UpdateEmptyVisibility();
            return;
        }

        StopTextTransition();
        lyricsText.Text = text;
        lyricsText.Opacity = targetOpacity;
        lyricsTextTransform.Y = 0;
        UpdateEmptyVisibility();
        if (!string.IsNullOrWhiteSpace(text))
        {
            StartTextTransition(targetOpacity);
        }
    }

    private void CancelCurrentSearch()
    {
        var searchCts = Interlocked.Exchange(ref _searchCts, null);
        CancelSearch(searchCts);
    }

    private void CancelSearch(CancellationTokenSource? searchCts)
    {
        if (searchCts == null)
        {
            return;
        }

        try
        {
            searchCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void StartTextTransition(double targetOpacity)
    {
        lyricsText.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation
            {
                From = 0,
                To = targetOpacity,
                Duration = LyricsTransitionDuration,
                EasingFunction = LyricsTransitionEasing,
                FillBehavior = FillBehavior.Stop
            });
        lyricsTextTransform.BeginAnimation(
            System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation
            {
                From = 4,
                To = 0,
                Duration = LyricsTransitionDuration,
                EasingFunction = LyricsTransitionEasing,
                FillBehavior = FillBehavior.Stop
            });
    }

    private void StopTextTransition()
    {
        lyricsText.BeginAnimation(OpacityProperty, null);
        lyricsTextTransform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
    }

    private void UpdateEmptyVisibility()
    {
        var isPaused = false;
        lock (_syncLock)
        {
            isPaused = _playbackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused;
        }

        LyricsGrid.Visibility = (Settings.IsHideWhenPaused && isPaused) ||
                                (Settings.IsHideWhenEmpty && string.IsNullOrWhiteSpace(lyricsText.Text))
            ? Visibility.Collapsed
            : Visibility.Visible;
    }
}
