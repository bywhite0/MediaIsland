using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using MediaIsland.Controls;
using MediaIsland.Helpers;
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
    // AMLL main line: opacity 0.3s with 0.1s delay; background vocal: 0.5s / 0.25s delay to 0.4.
    private const double TransitionDurationMs = 300;
    private const double TransitionDelayMs = 100;
    private const double BackgroundTransitionDurationMs = 500;
    private const double BackgroundTransitionDelayMs = 250;
    private const double BackgroundActiveOpacity = 0.4;
    private static readonly TimeSpan LineRenderInterval = TimeSpan.FromMilliseconds(80);
    private static readonly TimeSpan TransitionFrameInterval = TimeSpan.FromMilliseconds(16);

    private readonly IMediaService _mediaService;
    private readonly LyricsSearchService _lyricsSearchService;
    private readonly ILogger<LyricsComponent> _logger;
    private readonly DispatcherTimer _lyricsTimer;
    private readonly DispatcherTimer _transitionTimer;
    private readonly LyricsPlaybackClock _clock = new();
    private readonly object _syncLock = new();
    private readonly List<ActiveLineVisual> _activeLineVisuals = [];
    private readonly LyricsSearchSkipTracker _lyricsSearchSkipTracker = new();

    private StackPanel _front = null!;
    private StackPanel _back = null!;
    private TransitionState? _transition;
    private readonly List<(Control Control, double TargetOpacity)> _backgroundFadeTargets = [];
    private InterludeDotsPresenter? _interludeDots;
    private string _displayedStatusText = string.Empty;
    private double _displayedStatusOpacity = -1;
    private bool _isShowingInterlude;
    private bool _isShowingActiveLines;

    private LyricsDocument? _currentLyrics;
    private int[] _activeLineIndices = [];
    private string? _lastTitle;
    private string? _lastArtist;
    private string _lastStatusText = string.Empty;
    private bool _isCurrentTextStatus = true;
    private bool _isWordMode;
    private bool _isInterludeAnimationActive;
    private bool _isLoaded;
    private PluginSettings? _pluginSettings;
    private CancellationTokenSource? _searchCts;
    private int _searchVersion;

    public LyricsComponent(
        IMediaService mediaService,
        LyricsSearchService lyricsSearchService,
        ILogger<LyricsComponent> logger)
    {
        InitializeComponent();
        _front = LyricsFrontLayer;
        _back = LyricsBackLayer;
        _mediaService = mediaService;
        _lyricsSearchService = lyricsSearchService;
        _logger = logger;
        _lyricsTimer = new DispatcherTimer
        {
            Interval = LineRenderInterval
        };
        _lyricsTimer.Tick += LyricsTimer_OnTick;
        _transitionTimer = new DispatcherTimer
        {
            Interval = TransitionFrameInterval
        };
        _transitionTimer.Tick += TransitionTimer_OnTick;
    }

    private async void LyricsComponent_OnLoaded(object? sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        _mediaService.MediaInfoChanged -= MediaService_OnMediaInfoChanged;
        _mediaService.MediaInfoChanged += MediaService_OnMediaInfoChanged;
        _lyricsSearchService.CandidateApplied -= LyricsSearchService_OnCandidateApplied;
        _lyricsSearchService.CandidateApplied += LyricsSearchService_OnCandidateApplied;
        Settings.PropertyChanged += Settings_OnPropertyChanged;
        _pluginSettings = Plugin.Instance?.Settings;
        if (_pluginSettings != null)
        {
            _pluginSettings.PropertyChanged -= PluginSettings_OnPropertyChanged;
            _pluginSettings.PropertyChanged += PluginSettings_OnPropertyChanged;
            _pluginSettings.MediaSourceSettingsSaved -= PluginSettings_OnMediaSourceSettingsSaved;
            _pluginSettings.MediaSourceSettingsSaved += PluginSettings_OnMediaSourceSettingsSaved;
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
        _lyricsSearchService.CandidateApplied -= LyricsSearchService_OnCandidateApplied;
        Settings.PropertyChanged -= Settings_OnPropertyChanged;
        if (_pluginSettings != null)
        {
            _pluginSettings.PropertyChanged -= PluginSettings_OnPropertyChanged;
            _pluginSettings.MediaSourceSettingsSaved -= PluginSettings_OnMediaSourceSettingsSaved;
            _pluginSettings = null;
        }
        CancelCurrentSearch();
        CompleteTransition();
        lock (_syncLock)
        {
            _currentLyrics = null;
            _activeLineIndices = [];
            _isWordMode = false;
            _isInterludeAnimationActive = false;
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
            return;
        }

        if (e.PropertyName == nameof(LyricsComponentConfig.RenderFrameRate))
        {
            UpdateRenderCadence();
        }
    }

    private void PluginSettings_OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PluginSettings.IsWordLyricsEnabled))
        {
            Dispatcher.UIThread.Post(() =>
            {
                lock (_syncLock)
                {
                    _activeLineIndices = [];
                }

                RenderCurrentPositionOnce();
                UpdateRenderCadence();
            });
            return;
        }

        if (e.PropertyName == nameof(PluginSettings.IsLyricsInterludeAnimationEnabled))
        {
            Dispatcher.UIThread.Post(() =>
            {
                lock (_syncLock)
                {
                    _activeLineIndices = [];
                    _isInterludeAnimationActive = false;
                }

                RenderCurrentPositionOnce();
                UpdateRenderCadence();
            });
            return;
        }

        if (e.PropertyName == nameof(PluginSettings.IsLyricsTransitionEnabled))
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsLyricsTransitionEnabled)
                {
                    CompleteTransition();
                }
            });
            return;
        }

        if (e.PropertyName != nameof(PluginSettings.IsWordLyricsLiftEnabled) &&
            e.PropertyName != nameof(PluginSettings.IsWordLyricsEmphasisEnabled) &&
            e.PropertyName != nameof(PluginSettings.IsWordLyricsEdgeFeatherEnabled))
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            foreach (var visual in _activeLineVisuals)
            {
                if (visual.WordPresenter != null)
                {
                    ApplyWordLyricsAnimationSettings(visual.WordPresenter);
                }
            }
        });
    }

    private void PluginSettings_OnMediaSourceSettingsSaved(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_mediaService.CurrentMediaInfo is { } mediaInfo)
            {
                _ = HandleMediaInfoAsync(mediaInfo);
            }
        });
    }

    private bool CanSearchLyrics(MediaInfo info)
    {
        if (!MediaSourceFilter.IsEnabled(info.SourceApp, _pluginSettings?.MediaSourceList))
        {
            SkipLyricsSearch(info, "当前媒体来源已禁用", "已禁用");
            return false;
        }

        if (!MediaSourceFilter.IsLyricsSearchEnabled(info.SourceApp, _pluginSettings?.MediaSourceList))
        {
            SkipLyricsSearch(info, "当前媒体来源已禁用歌词搜索", "已禁用歌词搜索");
            return false;
        }

        _lyricsSearchSkipTracker.Reset();
        return true;
    }

    private void SkipLyricsSearch(MediaInfo info, string status, string reason)
    {
        if (!_lyricsSearchSkipTracker.TryRegister(info.SourceApp, reason))
        {
            return;
        }

        _logger.LogInformation("当前媒体会话 [{SourceApp}] {Reason}，跳过歌词搜索", info.SourceApp, reason);
        ClearLyrics(status);
    }

    private void ResetSkippedLyricsSearchState()
    {
        _lyricsSearchSkipTracker.Reset();
    }

    private async void MediaService_OnMediaInfoChanged(object? sender, MediaInfoChangedEventArgs e)
    {
        if (!_isLoaded)
        {
            return;
        }

        if (e.MediaInfo == null)
        {
            ResetSkippedLyricsSearchState();
            ClearLyrics("没有可用的媒体会话");
            return;
        }

        if (!CanSearchLyrics(e.MediaInfo))
        {
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
                ResetSkippedLyricsSearchState();
                ClearLyrics("没有可用的媒体会话");
                return;
            }

            if (!CanSearchLyrics(info))
            {
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
                    _isInterludeAnimationActive = false;
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
                    _isInterludeAnimationActive = false;
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

    private void LyricsSearchService_OnCandidateApplied(
        object? sender,
        LyricsSearchResultChangedEventArgs e)
    {
        if (e.Result == null)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (!_isLoaded ||
                !ReferenceEquals(_lyricsSearchService.GetCurrentResultFor(_mediaService.CurrentMediaInfo), e.Result))
            {
                return;
            }

            Interlocked.Increment(ref _searchVersion);
            CancelCurrentSearch();
            lock (_syncLock)
            {
                _currentLyrics = e.Result.Document;
                _activeLineIndices = [];
                _isWordMode = e.Result.Document.SyncMode == LyricsSyncMode.Word;
                _isInterludeAnimationActive = false;
            }

            SetStatus(string.Empty);
            RenderCurrentPositionOnce();
            UpdateRenderCadence();
        });
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
            wordMode = _isWordMode && IsWordLyricsEnabled;
        }

        if (lyrics == null || lyrics.Lines.Count == 0)
        {
            SetInterludeAnimationActive(false);
            return;
        }

        position = _clock.GetCurrentPosition() +
                   (_pluginSettings?.Lyrics.GetGlobalOffset(lyrics.Source) ?? TimeSpan.Zero);
        var activeLines = LyricsLineSelector.SelectActive(lyrics, position);
        var currentIndex = activeLines.Count > 0 ? activeLines[0].LineIndex : 0;
        var interlude = IsLyricsInterludeAnimationEnabled
            ? LyricsInterludeDetector.ComputeCurrentInterlude(lyrics.Lines, position, currentIndex)
            : null;
        var nextLinePreview = IsLyricsInterludeAnimationEnabled
            ? LyricsInterludeDetector.ComputeNextLinePreview(lyrics.Lines, position, currentIndex)
            : null;
        SetInterludeAnimationActive(interlude != null);
        if (interlude != null)
        {
            lock (_syncLock)
            {
                _activeLineIndices = [];
            }

            UpdateInterlude(interlude, position);
            return;
        }

        if (nextLinePreview != null)
        {
            UpdateActiveLines([nextLinePreview], wordMode, position);
            return;
        }

        if (activeLines.Count == 0)
        {
            HideInterludeDots();
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

    private void UpdateInterlude(LyricsInterlude interlude, TimeSpan position)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_isLoaded)
            {
                return;
            }

            if (_isShowingInterlude && _interludeDots != null)
            {
                _interludeDots.StartTime = interlude.StartTime;
                _interludeDots.EndTime = interlude.EndTime;
                _interludeDots.Position = position;
                _interludeDots.HorizontalAlignment = interlude.IsNextDuet
                    ? HorizontalAlignment.Right
                    : HorizontalAlignment.Center;
                _isCurrentTextStatus = false;
                UpdateEmptyVisibility();
                return;
            }

            RebuildInterludeVisual(interlude, position);
            _isCurrentTextStatus = false;
            UpdateEmptyVisibility();
        });
    }

    private void HideInterludeDots()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_isShowingInterlude)
            {
                return;
            }

            ClearActiveLineVisuals();
            UpdateEmptyVisibility();
        });
    }

    private void RebuildActiveLineVisuals(
        IReadOnlyList<LyricsLineSelection> activeLines,
        bool wordMode,
        TimeSpan position)
    {
        CompleteTransition();
        _activeLineVisuals.Clear();
        _backgroundFadeTargets.Clear();
        _interludeDots = null;
        _isShowingInterlude = false;
        _isShowingActiveLines = true;
        _displayedStatusText = string.Empty;
        _displayedStatusOpacity = -1;
        _back.Children.Clear();

        var hasDuet = activeLines.Any(item => item.IsDuetSide);
        var isMultiLine = activeLines.Count > 1;
        _back.Spacing = isMultiLine ? 0 : 1;

        foreach (var selection in activeLines)
        {
            var line = selection.Line;
            var fontSize = LyricsLayoutMetrics.GetActiveLineFontSize(
                LyricsText.FontSize,
                activeLines.Count,
                line.IsBackground);
            var opacity = line.IsBackground ? BackgroundActiveOpacity : 1.0;
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
                    MaxWidth = 520,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Opacity = opacity
                };
                ApplyWordLyricsAnimationSettings(wordPresenter);
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

            _back.Children.Add(visual);
            _activeLineVisuals.Add(new ActiveLineVisual(selection.LineIndex, wordPresenter));
            if (line.IsBackground)
            {
                if (IsLyricsTransitionEnabled)
                {
                    visual.Opacity = 0.0001;
                }

                _backgroundFadeTargets.Add((visual, opacity));
            }
        }

        StartTransition();
    }

    private void RebuildInterludeVisual(LyricsInterlude interlude, TimeSpan position)
    {
        CompleteTransition();
        _activeLineVisuals.Clear();
        _backgroundFadeTargets.Clear();
        _isShowingActiveLines = false;
        _isShowingInterlude = true;
        _displayedStatusText = string.Empty;
        _displayedStatusOpacity = -1;
        _back.Children.Clear();
        _back.Spacing = 1;

        var dots = new InterludeDotsPresenter
        {
            Foreground = LyricsText.Foreground,
            FontSize = LyricsText.FontSize,
            HorizontalAlignment = interlude.IsNextDuet
                ? HorizontalAlignment.Right
                : HorizontalAlignment.Center,
            StartTime = interlude.StartTime,
            EndTime = interlude.EndTime,
            Position = position
        };
        _interludeDots = dots;
        _back.Children.Add(dots);
        StartTransition();
    }

    private bool IsWordLyricsEnabled => _pluginSettings?.IsWordLyricsEnabled ?? true;

    private bool IsLyricsInterludeAnimationEnabled =>
        _pluginSettings?.IsLyricsInterludeAnimationEnabled ?? true;

    private bool IsLyricsTransitionEnabled =>
        _pluginSettings?.IsLyricsTransitionEnabled ?? true;

    private void ApplyWordLyricsAnimationSettings(WordLyricsPresenter presenter)
    {
        presenter.IsWordLiftEnabled = _pluginSettings?.IsWordLyricsLiftEnabled ?? true;
        presenter.IsWordEmphasisEnabled = _pluginSettings?.IsWordLyricsEmphasisEnabled ?? true;
        presenter.IsWordEdgeFeatherEnabled = _pluginSettings?.IsWordLyricsEdgeFeatherEnabled ?? true;
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
        bool isInterludeAnimationActive;
        lock (_syncLock)
        {
            wordMode = _isWordMode && IsWordLyricsEnabled;
            hasLyrics = _currentLyrics is { Lines.Count: > 0 };
            isInterludeAnimationActive = _isInterludeAnimationActive;
        }

        var playing = _clock.IsPlaying;
        _lyricsTimer.Interval = (wordMode || isInterludeAnimationActive) && playing
            ? TimeSpan.FromSeconds(1d / Settings.RenderFrameRate)
            : LineRenderInterval;
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
            _isInterludeAnimationActive = false;
        }
        UpdateRenderCadence();

        Dispatcher.UIThread.Post(() =>
        {
            if (!_isLoaded)
            {
                return;
            }

            ClearActiveLineVisuals();
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

            _isCurrentTextStatus = true;
            SetTextCore(GetVisibleText(text, isStatusText: true), isStatusText: true);
        });
    }

    private void ClearActiveLineVisuals()
    {
        CompleteTransition();
        _front.Children.Clear();
        _back.Children.Clear();
        _activeLineVisuals.Clear();
        _backgroundFadeTargets.Clear();
        _interludeDots = null;
        _isShowingInterlude = false;
        _isShowingActiveLines = false;
        _displayedStatusText = string.Empty;
        _displayedStatusOpacity = -1;
        lock (_syncLock)
        {
            _activeLineIndices = [];
        }
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
        if (_isCurrentTextStatus &&
            !_isShowingActiveLines &&
            !_isShowingInterlude &&
            string.Equals(_displayedStatusText, text, StringComparison.Ordinal) &&
            Math.Abs(_displayedStatusOpacity - targetOpacity) < 0.001)
        {
            UpdateEmptyVisibility();
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            // Empty status should not leave a blank layer; the next lyrics frame will transition in.
            ClearActiveLineVisuals();
            UpdateEmptyVisibility();
            return;
        }

        CompleteTransition();
        _activeLineVisuals.Clear();
        _backgroundFadeTargets.Clear();
        _interludeDots = null;
        _isShowingInterlude = false;
        _isShowingActiveLines = false;
        _displayedStatusText = text;
        _displayedStatusOpacity = targetOpacity;
        lock (_syncLock)
        {
            _activeLineIndices = [];
        }
        _back.Children.Clear();
        _back.Spacing = 1;
        _back.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = LyricsText.FontSize,
            Foreground = LyricsText.Foreground,
            Opacity = targetOpacity,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center
        });
        StartTransition();
        UpdateEmptyVisibility();
    }

    private void StartTransition()
    {
        var oldLayer = _front;
        var newLayer = _back;
        var oldTransform = (TranslateTransform)oldLayer.RenderTransform!;
        var newTransform = (TranslateTransform)newLayer.RenderTransform!;

        _front = newLayer;
        _back = oldLayer;

        if (!IsLyricsTransitionEnabled)
        {
            SettleBackgroundFadeTargets();
            CompleteTransition();
            return;
        }

        var offset = Math.Max(20, LyricsText.FontSize * 1.5);
        oldLayer.Opacity = oldLayer.Children.Count == 0 ? 0 : 1;
        oldTransform.Y = 0;
        newLayer.Opacity = 0;
        newTransform.Y = offset;
        newLayer.IsVisible = true;
        oldLayer.IsVisible = true;

        _transition = new TransitionState(
            oldLayer,
            oldTransform,
            newLayer,
            newTransform,
            Environment.TickCount64,
            offset);
        _transitionTimer.Start();
    }

    private void TransitionTimer_OnTick(object? sender, EventArgs e)
    {
        var transition = _transition;
        if (transition == null)
        {
            return;
        }

        var elapsedMs = Environment.TickCount64 - transition.StartedAtTick;
        var progress = Math.Clamp((elapsedMs - TransitionDelayMs) / TransitionDurationMs, 0, 1);
        var eased = 1 - Math.Pow(1 - progress, 3);
        transition.OldLayer.Opacity = 1 - eased;
        transition.OldTransform.Y = -transition.Offset * eased;
        transition.NewLayer.Opacity = eased;
        transition.NewTransform.Y = transition.Offset * (1 - eased);

        // Background vocals: delayed fade-in to a softer final opacity (AMLL ~0.4).
        var backgroundProgress = Math.Clamp(
            (elapsedMs - BackgroundTransitionDelayMs) / BackgroundTransitionDurationMs,
            0,
            1);
        // Approximate cubic-bezier(0, 1, 0, 1): slow start then settle quickly.
        var backgroundEased = backgroundProgress <= 0
            ? 0
            : 1 - Math.Pow(1 - backgroundProgress, 4);
        foreach (var (control, targetOpacity) in _backgroundFadeTargets)
        {
            control.Opacity = targetOpacity * backgroundEased;
        }

        if (progress >= 1 && backgroundProgress >= 1)
        {
            CompleteTransition();
        }
    }

    private void CompleteTransition()
    {
        _transitionTimer.Stop();
        _transition = null;
        _front.IsVisible = true;
        _front.Opacity = 1;
        ((TranslateTransform)_front.RenderTransform!).Y = 0;
        SettleBackgroundFadeTargets();
        _back.Children.Clear();
        _back.IsVisible = false;
        _back.Opacity = 0;
        ((TranslateTransform)_back.RenderTransform!).Y = 0;
    }

    private void SettleBackgroundFadeTargets()
    {
        foreach (var (control, targetOpacity) in _backgroundFadeTargets)
        {
            control.Opacity = targetOpacity;
        }
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
        var hasText = _front.Children.Count > 0 ||
                      (_transition != null && _back.Children.Count > 0);
        LyricsGrid.IsVisible = !Settings.IsHideWhenEmpty || hasText;
    }

    private void SetInterludeAnimationActive(bool isActive)
    {
        var changed = false;
        lock (_syncLock)
        {
            if (_isInterludeAnimationActive != isActive)
            {
                _isInterludeAnimationActive = isActive;
                changed = true;
            }
        }

        if (changed)
        {
            UpdateRenderCadence();
        }
    }

    private sealed record ActiveLineVisual(int LineIndex, WordLyricsPresenter? WordPresenter);

    private sealed record TransitionState(
        StackPanel OldLayer,
        TranslateTransform OldTransform,
        StackPanel NewLayer,
        TranslateTransform NewTransform,
        long StartedAtTick,
        double Offset);
}

internal sealed class LyricsSearchSkipTracker
{
    private readonly object _syncRoot = new();
    private string? _sourceApp;
    private string? _reason;

    public bool TryRegister(string sourceApp, string reason)
    {
        lock (_syncRoot)
        {
            if (string.Equals(_sourceApp, sourceApp, StringComparison.Ordinal) &&
                string.Equals(_reason, reason, StringComparison.Ordinal))
            {
                return false;
            }

            _sourceApp = sourceApp;
            _reason = reason;
            return true;
        }
    }

    public void Reset()
    {
        lock (_syncRoot)
        {
            _sourceApp = null;
            _reason = null;
        }
    }
}
