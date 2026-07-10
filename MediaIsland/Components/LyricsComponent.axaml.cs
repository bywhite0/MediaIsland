using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using Lyricify.Lyrics.Models;
using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Media;
using Microsoft.Extensions.Logging;
using RoutedEventArgs = Avalonia.Interactivity.RoutedEventArgs;

namespace MediaIsland.Components;

[ComponentInfo(
    "A681FD00-04F7-4E7B-9236-FAC85780D518",
    "实时歌词",
    "\uEBC9",
    "根据当前播放媒体搜索并显示同步歌词。"
)]
public partial class LyricsComponent : ComponentBase<LyricsComponentConfig>
{
    private readonly IMediaService _mediaService;
    private readonly LyricsSearchService _lyricsSearchService;
    private readonly ILogger<LyricsComponent> _logger;
    private readonly DispatcherTimer _lyricsTimer;
    private readonly object _syncLock = new();

    private LyricsData? _currentLyrics;
    private string? _currentLine;
    private string? _lastTitle;
    private string? _lastArtist;
    private TimeSpan _lastMediaPosition;
    private long _lastMediaUpdateTime = Environment.TickCount64;
    private bool _isPlaying;
    private double _playbackRate = 1.0;
    private bool _isLoaded;
    private CancellationTokenSource? _searchCts;
    private int _searchVersion;

    public LyricsComponent(
        IMediaService mediaService,
        LyricsSearchService lyricsSearchService,
        ILogger<LyricsComponent> logger)
    {
        InitializeComponent();
        _mediaService = mediaService;
        _lyricsSearchService = lyricsSearchService;
        _logger = logger;
        _lyricsTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(80)
        };
        _lyricsTimer.Tick += LyricsTimer_OnTick;
    }

    private async void LyricsComponent_OnLoaded(object? sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        _mediaService.MediaInfoChanged -= MediaService_OnMediaInfoChanged;
        _mediaService.MediaInfoChanged += MediaService_OnMediaInfoChanged;
        Settings.PropertyChanged += Settings_OnPropertyChanged;

        _lyricsTimer.Start();
        SetStatus("等待媒体信息...");

        try
        {
            await _mediaService.EnsureStartedAsync();
            await HandleMediaInfoAsync(_mediaService.CurrentMediaInfo);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Lyrics] Failed to start media service.");
            ClearLyrics("无法获取媒体信息");
        }
    }

    private void LyricsComponent_OnUnloaded(object? sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        _lyricsTimer.Stop();
        _mediaService.MediaInfoChanged -= MediaService_OnMediaInfoChanged;
        Settings.PropertyChanged -= Settings_OnPropertyChanged;
        CancelCurrentSearch();
    }

    private void Settings_OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LyricsComponentConfig.IsHideWhenEmpty) or nameof(LyricsComponentConfig.IsShowStatusText))
        {
            Dispatcher.UIThread.InvokeAsync(UpdateEmptyVisibility);
        }
    }

    private async void MediaService_OnMediaInfoChanged(object? sender, MediaInfoChangedEventArgs e)
    {
        if (!_isLoaded)
        {
            return;
        }

        if (e.MediaInfo == null)
        {
            ClearLyrics("没有可用的媒体会话");
            return;
        }

        UpdateMediaClock(e.MediaInfo);
        switch (e.ChangeKind)
        {
            case MediaInfoChangeKind.CurrentSession:
            case MediaInfoChangeKind.MediaProperties:
                await HandleMediaInfoAsync(e.MediaInfo);
                break;
            case MediaInfoChangeKind.Playback:
                _logger.LogInformation("[Lyrics] Playback status: {Status}", e.MediaInfo.PlaybackInfo.PlaybackState);
                break;
            case MediaInfoChangeKind.Timeline:
                break;
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

            UpdateMediaClock(info);
            if (string.Equals(info.Title, _lastTitle, StringComparison.Ordinal) &&
                string.Equals(info.Artist, _lastArtist, StringComparison.Ordinal))
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
                    "[Lyrics] Metadata: {Title} - {Artist} ({Album}), duration {Duration}, source {Source}",
                    info.Title,
                    info.Artist,
                    info.AlbumTitle,
                    info.Duration,
                    info.SourceApp);
                SetStatus($"正在查找歌词: {info.Title ?? "未知标题"}");

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

                SetStatus(lyrics == null ? "未找到歌词" : string.Empty);
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

    private void UpdateMediaClock(MediaInfo info)
    {
        lock (_syncLock)
        {
            _lastMediaPosition = info.Position;
            _lastMediaUpdateTime = Environment.TickCount64;
            _isPlaying = info.PlaybackInfo.PlaybackState == MediaPlaybackState.Playing;
            _playbackRate = info.PlaybackInfo.PlaybackRate ?? 1.0;
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

            currentMs = _lastMediaPosition.TotalMilliseconds;
            if (_isPlaying)
            {
                var elapsed = Environment.TickCount64 - _lastMediaUpdateTime;
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

        SetText(string.IsNullOrWhiteSpace(line.Text) ? string.Empty : line.Text, isStatusText: false);
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
            _lastMediaPosition = TimeSpan.Zero;
            _lastMediaUpdateTime = Environment.TickCount64;
            _isPlaying = false;
            _playbackRate = 1.0;
        }

        SetStatus(status);
    }

    private void SetStatus(string text)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!_isLoaded || Settings == null)
            {
                return;
            }

            SetTextCore(Settings.IsShowStatusText ? text : string.Empty, isStatusText: true);
        });
    }

    private void SetText(string text, bool isStatusText)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!_isLoaded)
            {
                return;
            }

            SetTextCore(text, isStatusText);
        });
    }

    private void SetTextCore(string text, bool isStatusText)
    {
        var targetOpacity = isStatusText ? 0.72 : 1.0;
        if (LyricsText.Text == text && Math.Abs(LyricsText.Opacity - targetOpacity) < 0.001)
        {
            UpdateEmptyVisibility();
            return;
        }

        LyricsText.Text = text;
        LyricsText.Opacity = targetOpacity;
        UpdateEmptyVisibility();
    }

    private void CancelCurrentSearch()
    {
        var searchCts = Interlocked.Exchange(ref _searchCts, null);
        CancelSearch(searchCts);
    }

    private static void CancelSearch(CancellationTokenSource? searchCts)
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

    private void UpdateEmptyVisibility()
    {
        LyricsGrid.IsVisible = !Settings.IsHideWhenEmpty || !string.IsNullOrWhiteSpace(LyricsText.Text);
    }
}
