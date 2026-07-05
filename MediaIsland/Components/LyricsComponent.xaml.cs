using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
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

    private LyricsData? _currentLyrics;
    private string? _currentLine;
    private string? _lastTitle;
    private string? _lastArtist;
    private TimeSpan _lastSmtcPosition;
    private DateTime _lastSmtcUpdateTime = DateTime.Now;
    private bool _isPlaying;
    private double _playbackRate = 1.0;
    private int _searchVersion;

    public LyricsComponent(IMediaService mediaService, ILogger<LyricsComponent> logger)
    {
        InitializeComponent();
        _mediaService = mediaService;
        _logger = logger;
        _lyricsSearchService = new LyricsSearchService(logger);
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
    }

    private void Settings_OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LyricsComponentConfig.IsHideWhenEmpty) or nameof(LyricsComponentConfig.IsShowStatusText))
        {
            UpdateEmptyVisibility();
        }
    }

    private async void MediaService_OnMediaPropertiesChanged(object? sender, MediaInfo? info)
    {
        await HandleMediaInfoAsync(info);
    }

    private void MediaService_OnTimelinePropertyChanged(object? sender, GlobalSystemMediaTransportControlsSessionTimelineProperties timeline)
    {
        lock (_syncLock)
        {
            _lastSmtcPosition = timeline.Position;
            _lastSmtcUpdateTime = DateTime.Now;
        }
    }

    private void MediaService_OnPlaybackStateChanged(object? sender, GlobalSystemMediaTransportControlsSessionPlaybackInfo playbackInfo)
    {
        lock (_syncLock)
        {
            _isPlaying = playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            _playbackRate = playbackInfo.PlaybackRate ?? 1.0;
        }

        _logger.LogInformation("[Lyrics] Playback status: {Status}", playbackInfo.PlaybackStatus);
    }

    private void MediaService_OnFocusedSessionChanged(object? sender, EventArgs e)
    {
        if (_mediaService.CurrentMediaInfo == null)
        {
            ClearLyrics("没有可用的媒体会话");
        }
    }

    private async Task HandleMediaInfoAsync(MediaInfo? info)
    {
        if (info == null)
        {
            ClearLyrics("没有可用的媒体会话");
            return;
        }

        lock (_syncLock)
        {
            _lastSmtcPosition = info.Position;
            _lastSmtcUpdateTime = DateTime.Now;
            _isPlaying = info.PlaybackInfo?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            _playbackRate = info.PlaybackInfo?.PlaybackRate ?? 1.0;
        }

        if (info.Title == _lastTitle && info.Artist == _lastArtist)
        {
            return;
        }

        _lastTitle = info.Title;
        _lastArtist = info.Artist;
        var version = Interlocked.Increment(ref _searchVersion);

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

        var result = await _lyricsSearchService.SearchAsync(info);
        if (version != _searchVersion)
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
                var elapsed = DateTime.Now - _lastSmtcUpdateTime;
                currentMs += elapsed.TotalMilliseconds * _playbackRate;
            }
        }

        var line = lyrics.Lines?
            .Where(lineInfo => lineInfo.StartTime <= currentMs)
            .OrderByDescending(lineInfo => lineInfo.StartTime)
            .FirstOrDefault();

        if (line == null || line.Text == _currentLine)
        {
            return;
        }

        _currentLine = line.Text;
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
        _lastTitle = null;
        _lastArtist = null;

        lock (_syncLock)
        {
            _currentLyrics = null;
            _currentLine = null;
            _lastSmtcPosition = TimeSpan.Zero;
            _lastSmtcUpdateTime = DateTime.Now;
            _isPlaying = false;
            _playbackRate = 1.0;
        }

        SetStatus(status);
    }

    private void SetStatus(string text)
    {
        SetText(Settings.IsShowStatusText ? text : string.Empty, isStatusText: true);
    }

    private void SetText(string text, bool isStatusText)
    {
        Dispatcher.InvokeAsync(() =>
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
        });
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
        LyricsGrid.Visibility = Settings.IsHideWhenEmpty && string.IsNullOrWhiteSpace(lyricsText.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }
}
