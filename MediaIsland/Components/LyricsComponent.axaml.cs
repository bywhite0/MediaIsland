using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using MediaIsland.Models;
using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Lyrics.Models;
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
    private static readonly TimeSpan LyricsTransitionDuration = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan WordRenderInterval = TimeSpan.FromMilliseconds(33);

    private readonly IMediaService _mediaService;
    private readonly LyricsSearchService _lyricsSearchService;
    private readonly ILogger<LyricsComponent> _logger;
    private readonly DispatcherTimer _lyricsTimer;
    private readonly LyricsPlaybackClock _clock = new();
    private readonly TranslateTransform _lyricsTextTransform;
    private readonly object _syncLock = new();
    private readonly List<ActiveLineVisual> _activeLineVisuals = [];

    private LyricsDocument? _currentLyrics;
    private int[] _activeLineIndices = [];
    private string? _lastTitle;
    private string? _lastArtist;
    private string _lastStatusText = string.Empty;
    private bool _isCurrentTextStatus = true;
    private bool _isWordMode;
    private bool _isLoaded;
    private PluginSettings? _pluginSettings;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _textTransitionCts;
    private int _searchVersion;

    public LyricsComponent(
        IMediaService mediaService,
        LyricsSearchService lyricsSearchService,
        ILogger<LyricsComponent> logger)
    {
        InitializeComponent();
        _lyricsTextTransform = (TranslateTransform)LyricsText.RenderTransform!;
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
        _pluginSettings = Plugin.Instance?.Settings;
        if (_pluginSettings != null)
        {
            _pluginSettings.PropertyChanged -= PluginSettings_OnPropertyChanged;
            _pluginSettings.PropertyChanged += PluginSettings_OnPropertyChanged;
        }

        SetStatus("等待媒体信息...");
        UpdateRenderCadence();

        try
        {
            await _mediaService.EnsureStartedAsync();
            await HandleMediaInfoAsync(_mediaService.CurrentMediaInfo);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[歌词] 启动媒体服务失败。");
            ClearLyrics("无法获取媒体信息");
        }
    }

    private void LyricsComponent_OnUnloaded(object? sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        _lyricsTimer.Stop();
        _mediaService.MediaInfoChanged -= MediaService_OnMediaInfoChanged;
        Settings.PropertyChanged -= Settings_OnPropertyChanged;
        if (_pluginSettings != null)
        {
            _pluginSettings.PropertyChanged -= PluginSettings_OnPropertyChanged;
            _pluginSettings = null;
        }
        CancelCurrentSearch();
        StopTextTransition();
        lock (_syncLock)
        {
            _currentLyrics = null;
            _activeLineIndices = [];
            _isWordMode = false;
        }
        _clock.Reset();
    }

    private void Settings_OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LyricsComponentConfig.IsShowStatusText))
        {
            ApplyCurrentDisplaySettings();
            return;
        }

        if (e.PropertyName == nameof(LyricsComponentConfig.IsHideWhenEmpty))
        {
            Dispatcher.UIThread.InvokeAsync(UpdateEmptyVisibility);
        }
    }

    private void PluginSettings_OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PluginSettings.IsWordLyricsLiftEnabled))
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            var isEnabled = _pluginSettings?.IsWordLyricsLiftEnabled ?? true;
            foreach (var visual in _activeLineVisuals)
            {
                if (visual.WordPresenter != null)
                {
                    visual.WordPresenter.IsLiftEnabled = isEnabled;
                }
            }
        });
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

        _clock.Update(e.MediaInfo);
        switch (e.ChangeKind)
        {
            case MediaInfoChangeKind.CurrentSession:
            case MediaInfoChangeKind.MediaProperties:
                await HandleMediaInfoAsync(e.MediaInfo);
                break;
            case MediaInfoChangeKind.Playback:
                _logger.LogInformation("[歌词] 播放状态：{Status}", e.MediaInfo.PlaybackInfo.PlaybackState);
                RenderCurrentPositionOnce();
                UpdateRenderCadence();
                break;
            case MediaInfoChangeKind.Timeline:
                RenderCurrentPositionOnce();
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

            _clock.Update(info);
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
                    _activeLineIndices = [];
                    _isWordMode = false;
                }

                _logger.LogInformation(
                    "[歌词] 媒体信息：{Title} - {Artist}（{Album}），时长 {Duration}，来源 {Source}",
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

                var document = result?.Document;
                lock (_syncLock)
                {
                    _currentLyrics = document;
                    _activeLineIndices = [];
                    _isWordMode = document?.SyncMode == LyricsSyncMode.Word;
                }

                SetStatus(document == null ? "未找到歌词" : string.Empty);
                RenderCurrentPositionOnce();
                UpdateRenderCadence();
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
            _logger.LogError(ex, "[歌词] 处理媒体信息时发生错误。");
            ClearLyrics("查找歌词失败");
        }
    }

    private void LyricsTimer_OnTick(object? sender, EventArgs e)
    {
        RenderCurrentPositionOnce();
    }

    private void RenderCurrentPositionOnce()
    {
        LyricsDocument? lyrics;
        bool wordMode;
        TimeSpan position;

        lock (_syncLock)
        {
            lyrics = _currentLyrics;
            wordMode = _isWordMode;
        }

        if (lyrics == null || lyrics.Lines.Count == 0)
        {
            return;
        }

        position = _clock.GetCurrentPosition() +
                   (_pluginSettings?.Lyrics.GetGlobalOffset(lyrics.Source) ?? TimeSpan.Zero);
        var activeLines = LyricsLineSelector.SelectActive(lyrics, position);
        if (activeLines.Count == 0)
        {
            return;
        }

        UpdateActiveLines(activeLines, wordMode, position);
    }

    private void UpdateActiveLines(
        IReadOnlyList<LyricsLineSelection> activeLines,
        bool wordMode,
        TimeSpan position)
    {
        var lineIndices = activeLines.Select(item => item.LineIndex).ToArray();
        bool linesChanged;
        lock (_syncLock)
        {
            linesChanged = !_activeLineIndices.SequenceEqual(lineIndices);
            if (linesChanged)
            {
                _activeLineIndices = lineIndices;
            }
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (!_isLoaded)
            {
                return;
            }

            LyricsText.IsVisible = false;
            ActiveLyricsLines.IsVisible = true;
            if (linesChanged)
            {
                RebuildActiveLineVisuals(activeLines, wordMode, position);
            }
            else
            {
                foreach (var visual in _activeLineVisuals)
                {
                    if (visual.WordPresenter != null)
                    {
                        visual.WordPresenter.Position = position;
                    }
                }
            }

            _isCurrentTextStatus = false;
            UpdateEmptyVisibility();
        });
    }

    private void RebuildActiveLineVisuals(
        IReadOnlyList<LyricsLineSelection> activeLines,
        bool wordMode,
        TimeSpan position)
    {
        StopTextTransition();
        ActiveLyricsLines.Children.Clear();
        _activeLineVisuals.Clear();
        var hasDuet = activeLines.Any(item => item.IsDuetSide);
        var isMultiLine = activeLines.Count > 1;
        ActiveLyricsLines.Spacing = isMultiLine ? 0 : 1;

        foreach (var selection in activeLines)
        {
            var line = selection.Line;
            var fontSize = LyricsLayoutMetrics.GetActiveLineFontSize(
                LyricsText.FontSize,
                activeLines.Count,
                line.IsBackground);
            var opacity = line.IsBackground ? 0.72 : 1.0;
            var textAlignment = selection.IsDuetSide
                ? TextAlignment.Right
                : hasDuet
                    ? TextAlignment.Left
                    : TextAlignment.Center;
            WordLyricsPresenter? wordPresenter = null;
            Control visual;
            if (wordMode && line.Words.Count > 0)
            {
                wordPresenter = new WordLyricsPresenter
                {
                    Line = line,
                    Position = position,
                    FontSize = fontSize,
                    Foreground = LyricsText.Foreground,
                    TextAlignment = textAlignment,
                    IsLiftEnabled = _pluginSettings?.IsWordLyricsLiftEnabled ?? true,
                    MaxWidth = 520,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Opacity = opacity
                };
                AutomationProperties.SetName(wordPresenter, line.Text);
                visual = wordPresenter;
            }
            else
            {
                visual = new TextBlock
                {
                    Text = line.Text,
                    FontSize = fontSize,
                    Foreground = LyricsText.Foreground,
                    TextAlignment = textAlignment,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextWrapping = TextWrapping.NoWrap,
                    MaxWidth = 520,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Opacity = opacity
                };
            }

            if (isMultiLine)
            {
                visual.Margin = new Thickness(0, -1, 0, -1);
            }

            ActiveLyricsLines.Children.Add(visual);
            _activeLineVisuals.Add(new ActiveLineVisual(selection.LineIndex, wordPresenter));
        }

        ActiveLyricsLines.Opacity = 0;
        StartActiveLinesTransition();
    }

    private void UpdateRenderCadence()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(UpdateRenderCadence);
            return;
        }

        bool wordMode;
        bool hasLyrics;
        lock (_syncLock)
        {
            wordMode = _isWordMode;
            hasLyrics = _currentLyrics is { Lines.Count: > 0 };
        }

        var playing = _clock.IsPlaying;
        _lyricsTimer.Interval = wordMode && playing ? WordRenderInterval : TimeSpan.FromMilliseconds(80);
        if (_isLoaded && hasLyrics && playing)
        {
            if (!_lyricsTimer.IsEnabled)
            {
                _lyricsTimer.Start();
            }
        }
        else
        {
            _lyricsTimer.Stop();
        }
    }

    private void ClearLyrics(string status)
    {
        Interlocked.Increment(ref _searchVersion);
        CancelCurrentSearch();
        _lastTitle = null;
        _lastArtist = null;
        _clock.Reset();

        lock (_syncLock)
        {
            _currentLyrics = null;
            _activeLineIndices = [];
            _isWordMode = false;
        }
        UpdateRenderCadence();

        Dispatcher.UIThread.Post(() =>
        {
            if (!_isLoaded)
            {
                return;
            }

            ClearActiveLineVisuals();
            LyricsText.IsVisible = true;
        });

        SetStatus(status);
    }

    private void SetStatus(string text)
    {
        _lastStatusText = text;
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!_isLoaded || Settings == null)
            {
                return;
            }

            ClearActiveLineVisuals();
            LyricsText.IsVisible = true;
            _isCurrentTextStatus = true;
            SetTextCore(GetVisibleText(text, isStatusText: true), isStatusText: true);
        });
    }

    private void ClearActiveLineVisuals()
    {
        ActiveLyricsLines.IsVisible = false;
        ActiveLyricsLines.Children.Clear();
        _activeLineVisuals.Clear();
    }

    private void ApplyCurrentDisplaySettings()
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!_isLoaded || Settings == null)
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
        if (LyricsText.Text == text &&
            Math.Abs(LyricsText.GetBaseValue(Visual.OpacityProperty).GetValueOrDefault(LyricsText.Opacity) - targetOpacity) < 0.001)
        {
            UpdateEmptyVisibility();
            return;
        }

        StopTextTransition();
        LyricsText.Text = text;
        LyricsText.Opacity = targetOpacity;
        _lyricsTextTransform.Y = 0;
        UpdateEmptyVisibility();
        if (!string.IsNullOrWhiteSpace(text))
        {
            StartTextTransition(targetOpacity);
        }
    }

    private void StartTextTransition(double targetOpacity)
    {
        var transitionCts = new CancellationTokenSource();
        _textTransitionCts = transitionCts;
        _ = RunTextTransitionAsync(targetOpacity, transitionCts);
    }

    private void StartActiveLinesTransition()
    {
        var transitionCts = new CancellationTokenSource();
        _textTransitionCts = transitionCts;
        _ = RunActiveLinesTransitionAsync(transitionCts);
    }

    private async Task RunTextTransitionAsync(double targetOpacity, CancellationTokenSource transitionCts)
    {
        var opacityAnimation = CreateTextTransition(Visual.OpacityProperty, 0, targetOpacity);
        var offsetAnimation = CreateTextTransition(TranslateTransform.YProperty, 4, 0);

        try
        {
            await Task.WhenAll(
                opacityAnimation.RunAsync(LyricsText, transitionCts.Token),
                offsetAnimation.RunAsync(_lyricsTextTransform, transitionCts.Token));
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_textTransitionCts, transitionCts))
            {
                _textTransitionCts = null;
                LyricsText.Opacity = targetOpacity;
                _lyricsTextTransform.Y = 0;
            }

            transitionCts.Dispose();
        }
    }

    private async Task RunActiveLinesTransitionAsync(CancellationTokenSource transitionCts)
    {
        var opacityAnimation = CreateTextTransition(Visual.OpacityProperty, 0, 1);
        try
        {
            await opacityAnimation.RunAsync(ActiveLyricsLines, transitionCts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_textTransitionCts, transitionCts))
            {
                _textTransitionCts = null;
                ActiveLyricsLines.Opacity = 1;
            }

            transitionCts.Dispose();
        }
    }

    private static Animation CreateTextTransition(AvaloniaProperty property, double from, double to)
    {
        return new Animation
        {
            Duration = LyricsTransitionDuration,
            Easing = new CubicEaseOut(),
            FillMode = FillMode.None,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0),
                    Setters = { new Setter(property, from) }
                },
                new KeyFrame
                {
                    Cue = new Cue(1),
                    Setters = { new Setter(property, to) }
                }
            }
        };
    }

    private void StopTextTransition()
    {
        var transitionCts = _textTransitionCts;
        _textTransitionCts = null;
        transitionCts?.Cancel();
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
        var hasText = ActiveLyricsLines.IsVisible
            ? _activeLineVisuals.Count > 0
            : !string.IsNullOrWhiteSpace(LyricsText.Text);
        LyricsGrid.IsVisible = !Settings.IsHideWhenEmpty || hasText;
    }

    private sealed record ActiveLineVisual(int LineIndex, WordLyricsPresenter? WordPresenter);
}
