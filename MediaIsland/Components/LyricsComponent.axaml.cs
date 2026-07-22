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
    private Canvas _exitLayer = null!;
    private TransitionState? _transition;
    private readonly List<(Control Control, double TargetOpacity)> _backgroundFadeTargets = [];
    private readonly List<LineMotion> _lineMotions = [];
    private bool _backgroundOnlyFade;
    private long _backgroundOnlyFadeStartedAt;
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
        _exitLayer = LyricsExitLayer;
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
        int[] previousIndices;
        bool linesChanged;
        lock (_syncLock)
        {
            previousIndices = _activeLineIndices;
            linesChanged = !previousIndices.SequenceEqual(lineIndices);
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
                // Overlapping active sets use per-line enter/leave motion; only hard switches
                // (no shared indices) replay the full-component dual-layer slide/fade.
                var animateFullTransition = ShouldAnimateFullLineTransition(previousIndices, lineIndices);
                RebuildActiveLineVisuals(
                    activeLines,
                    wordMode,
                    position,
                    animateFullTransition,
                    previousIndices);
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

    private bool ShouldAnimateFullLineTransition(int[] previousIndices, int[] nextIndices) =>
        LyricsLayoutMetrics.ShouldAnimateFullLineTransition(
            _isShowingActiveLines,
            previousIndices,
            nextIndices);

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
        TimeSpan position,
        bool animateFullTransition = true,
        IReadOnlyList<int>? previousIndices = null)
    {
        _interludeDots = null;
        _isShowingInterlude = false;
        _isShowingActiveLines = true;
        _displayedStatusText = string.Empty;
        _displayedStatusOpacity = -1;

        // Shared-line handoff: morph previous layout into the next one instead of swapping the whole frame.
        if (!animateFullTransition &&
            IsLyricsTransitionEnabled &&
            _activeLineVisuals.Count > 0)
        {
            ApplyPartialLineTransition(activeLines, wordMode, position);
            return;
        }

        CompleteTransition();
        _activeLineVisuals.Clear();
        _backgroundFadeTargets.Clear();
        _lineMotions.Clear();

        var previousSet = previousIndices is { Count: > 0 }
            ? previousIndices.ToHashSet()
            : [];
        var targetLayer = animateFullTransition ? _back : _front;
        targetLayer.Children.Clear();

        var hasDuet = activeLines.Any(item => item.IsDuetSide);
        var isMultiLine = activeLines.Count > 1;
        targetLayer.Spacing = isMultiLine ? 0 : 1;

        foreach (var selection in activeLines)
        {
            var visual = CreateLineVisual(
                selection,
                activeLines.Count,
                hasDuet,
                isMultiLine,
                wordMode,
                position);
            targetLayer.Children.Add(visual.Control);
            _activeLineVisuals.Add(visual);
            if (visual.IsBackground)
            {
                var isNewBackground = !previousSet.Contains(selection.LineIndex);
                var shouldFadeBackground = IsLyricsTransitionEnabled &&
                                           (animateFullTransition || isNewBackground);
                if (shouldFadeBackground)
                {
                    visual.Control.Opacity = 0.0001;
                    _backgroundFadeTargets.Add((visual.Control, visual.TargetOpacity));
                }
            }
        }

        if (animateFullTransition)
        {
            StartTransition();
            return;
        }

        _front.IsVisible = true;
        _front.Opacity = 1;
        ((TranslateTransform)_front.RenderTransform!).Y = 0;
        _back.Children.Clear();
        _back.IsVisible = false;
        _back.Opacity = 0;
        ((TranslateTransform)_back.RenderTransform!).Y = 0;

        if (_backgroundFadeTargets.Count > 0)
        {
            StartBackgroundOnlyFade();
        }
        else
        {
            SettleBackgroundFadeTargets();
        }
    }

    private void ApplyPartialLineTransition(
        IReadOnlyList<LyricsLineSelection> activeLines,
        bool wordMode,
        TimeSpan position)
    {
        // Finish any whole-frame animation, but keep the current line controls for reuse.
        CompleteFullFrameTransitionOnly();
        FinishLineMotions(removeExiting: true, settleRemaining: true);
        ClearExitLayer();

        // Snapshot previous on-screen positions in host coordinates before rebuilding the final stack.
        var previousByIndex = _activeLineVisuals.ToDictionary(item => item.LineIndex);
        var firstFrames = previousByIndex.ToDictionary(
            item => item.Key,
            item => new LineFirstFrame(
                GetHostY(item.Value.Control),
                item.Value.Control.Bounds.Height,
                item.Value.Control.Opacity,
                item.Value.TargetOpacity,
                GetLineFontSize(item.Value)));

        var hasDuet = activeLines.Any(item => item.IsDuetSide);
        var isMultiLine = activeLines.Count > 1;
        var plan = PrecomputeLinePlan(activeLines, hasDuet, isMultiLine);
        var nextIndices = plan.Select(item => item.LineIndex).ToHashSet();
        var leaving = _activeLineVisuals
            .Where(item => !nextIndices.Contains(item.LineIndex))
            .ToList();

        var offset = Math.Max(12, LyricsText.FontSize * 0.75);
        var now = Environment.TickCount64;
        _lineMotions.Clear();
        _backgroundFadeTargets.Clear();
        _front.Spacing = isMultiLine ? 0 : 1;

        // Front stack only holds the final active set so layout is already the destination frame.
        var nextVisuals = new List<ActiveLineVisual>(plan.Count);
        var stayingControls = new List<ActiveLineVisual>();
        _front.Children.Clear();
        foreach (var planned in plan)
        {
            if (previousByIndex.TryGetValue(planned.LineIndex, out var kept))
            {
                var first = firstFrames[planned.LineIndex];
                UpdateLineVisual(
                    kept,
                    planned.Selection,
                    activeLines.Count,
                    hasDuet,
                    isMultiLine,
                    wordMode,
                    position);
                var transform = EnsureLineTransform(kept.Control);
                kept.Control.Opacity = first.Opacity;
                transform.Y = 0;
                _front.Children.Add(kept.Control);
                nextVisuals.Add(kept);
                stayingControls.Add(kept);
                continue;
            }

            var created = CreateLineVisual(
                planned.Selection,
                activeLines.Count,
                hasDuet,
                isMultiLine,
                wordMode,
                position);
            var enterTransform = EnsureLineTransform(created.Control);
            created.Control.Opacity = 0.0001;
            enterTransform.Y = offset;
            _front.Children.Add(created.Control);
            nextVisuals.Add(created);

            var delay = created.IsBackground ? BackgroundTransitionDelayMs : TransitionDelayMs;
            var duration = created.IsBackground ? BackgroundTransitionDurationMs : TransitionDurationMs;
            _lineMotions.Add(new LineMotion(
                created.Control,
                0.0001,
                created.TargetOpacity,
                offset,
                0,
                delay,
                duration,
                RemoveWhenDone: false,
                now,
                enterTransform,
                IsBackgroundStyle: created.IsBackground,
                RemoveFromExitLayer: false));
        }

        // Park leaving lines on an overlay at their original host Y so they do not reflow the stack.
        foreach (var leave in leaving)
        {
            var first = firstFrames[leave.LineIndex];
            if (leave.Control.Parent is Panel parent)
            {
                parent.Children.Remove(leave.Control);
            }

            var transform = EnsureLineTransform(leave.Control);
            transform.Y = 0;
            leave.Control.Opacity = first.Opacity;
            Canvas.SetLeft(leave.Control, 0);
            Canvas.SetTop(leave.Control, first.Y);
            _exitLayer.Children.Add(leave.Control);
            _lineMotions.Add(new LineMotion(
                leave.Control,
                first.Opacity,
                0,
                0,
                -offset,
                DelayMs: 0,
                DurationMs: TransitionDurationMs,
                RemoveWhenDone: true,
                now,
                transform,
                IsBackgroundStyle: leave.IsBackground,
                RemoveFromExitLayer: true));
        }

        _activeLineVisuals.Clear();
        _activeLineVisuals.AddRange(nextVisuals);
        _front.IsVisible = true;
        _front.Opacity = 1;
        ((TranslateTransform)_front.RenderTransform!).Y = 0;
        _back.Children.Clear();
        _back.IsVisible = false;
        _back.Opacity = 0;
        ((TranslateTransform)_back.RenderTransform!).Y = 0;

        // Final front layout is already the destination; FLIP shared lines from first host Y to last.
        ForceLineHostLayout();
        foreach (var kept in stayingControls)
        {
            if (!firstFrames.TryGetValue(kept.LineIndex, out var first))
            {
                continue;
            }

            var transform = EnsureLineTransform(kept.Control);
            var lastY = GetHostY(kept.Control);
            var deltaY = first.Y - lastY;
            if (Math.Abs(deltaY) < 0.5)
            {
                deltaY = EstimateSharedLineDeltaY(kept.LineIndex, firstFrames, plan);
            }

            transform.Y = deltaY;
            kept.Control.Opacity = first.Opacity;
            _lineMotions.Add(new LineMotion(
                kept.Control,
                first.Opacity,
                kept.TargetOpacity,
                deltaY,
                0,
                DelayMs: TransitionDelayMs * 0.5,
                DurationMs: TransitionDurationMs,
                RemoveWhenDone: false,
                StartedAtTick: now,
                transform,
                IsBackgroundStyle: kept.IsBackground,
                RemoveFromExitLayer: false));
        }

        if (_lineMotions.Count > 0)
        {
            _transitionTimer.Start();
        }
    }

    private double GetHostY(Control control)
    {
        var point = control.TranslatePoint(new Point(0, 0), LyricsContentHost);
        if (point.HasValue)
        {
            return point.Value.Y;
        }

        return control.Bounds.Y;
    }

    private void ClearExitLayer()
    {
        _exitLayer.Children.Clear();
    }

    private IReadOnlyList<PlannedLineState> PrecomputeLinePlan(
        IReadOnlyList<LyricsLineSelection> activeLines,
        bool hasDuet,
        bool isMultiLine)
    {
        var plan = new List<PlannedLineState>(activeLines.Count);
        for (var order = 0; order < activeLines.Count; order++)
        {
            var selection = activeLines[order];
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
            plan.Add(new PlannedLineState(
                selection.LineIndex,
                order,
                selection,
                fontSize,
                opacity,
                textAlignment,
                isMultiLine,
                line.IsBackground));
        }

        return plan;
    }

    private void ForceLineHostLayout()
    {
        var width = LyricsContentHost.Bounds.Width;
        if (width <= 0)
        {
            width = Math.Max(LyricsText.FontSize * 20, 240);
        }

        // Arrange the final front stack first, then the host so TranslatePoint host-Y is accurate
        // after multi-line -> single-line height changes (VerticalAlignment=Center).
        var available = new Size(width, double.PositiveInfinity);
        _front.Measure(available);
        var frontHeight = Math.Max(_front.DesiredSize.Height, 1);
        _front.Arrange(new Rect(0, 0, width, frontHeight));

        LyricsContentHost.Measure(available);
        var hostHeight = Math.Max(LyricsContentHost.DesiredSize.Height, frontHeight);
        var hostWidth = Math.Max(LyricsContentHost.DesiredSize.Width, width);
        var hostBounds = LyricsContentHost.Bounds;
        if (hostBounds.Width > 0 && hostBounds.Height > 0)
        {
            LyricsContentHost.Arrange(new Rect(hostBounds.X, hostBounds.Y, hostBounds.Width, hostBounds.Height));
        }
        else
        {
            LyricsContentHost.Arrange(new Rect(0, 0, hostWidth, hostHeight));
        }
    }

    private static double EstimateSharedLineDeltaY(
        int lineIndex,
        IReadOnlyDictionary<int, LineFirstFrame> firstFrames,
        IReadOnlyList<PlannedLineState> plan)
    {
        static double HeightOf(LineFirstFrame frame, double fallbackFontSize) =>
            frame.Height > 0 ? frame.Height : Math.Max(fallbackFontSize * 1.25, 14);

        double nextY = 0;
        foreach (var planned in plan)
        {
            if (planned.LineIndex == lineIndex)
            {
                break;
            }

            if (firstFrames.TryGetValue(planned.LineIndex, out var frame))
            {
                nextY += HeightOf(frame, planned.TargetFontSize);
            }
            else
            {
                nextY += Math.Max(planned.TargetFontSize * 1.25, 14);
            }
        }

        double previousY;
        if (firstFrames.TryGetValue(lineIndex, out var self) && self.Y > 0.5)
        {
            previousY = self.Y;
        }
        else
        {
            previousY = 0;
            foreach (var item in firstFrames.OrderBy(frame => frame.Value.Y).ThenBy(frame => frame.Key))
            {
                if (item.Key == lineIndex)
                {
                    break;
                }

                previousY += HeightOf(item.Value, 14);
            }
        }

        return previousY - nextY;
    }

    private static double GetLineFontSize(ActiveLineVisual visual)
    {
        if (visual.WordPresenter != null)
        {
            return visual.WordPresenter.FontSize;
        }

        return visual.Control is TextBlock textBlock ? textBlock.FontSize : 14;
    }

    private ActiveLineVisual CreateLineVisual(
        LyricsLineSelection selection,
        int visibleLineCount,
        bool hasDuet,
        bool isMultiLine,
        bool wordMode,
        TimeSpan position)
    {
        var line = selection.Line;
        var fontSize = LyricsLayoutMetrics.GetActiveLineFontSize(
            LyricsText.FontSize,
            visibleLineCount,
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
                Opacity = opacity,
                RenderTransform = new TranslateTransform()
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
                Opacity = opacity,
                RenderTransform = new TranslateTransform()
            };
        }

        if (isMultiLine)
        {
            visual.Margin = new Thickness(0, -1, 0, -1);
        }

        return new ActiveLineVisual(
            selection.LineIndex,
            visual,
            wordPresenter,
            opacity,
            line.IsBackground);
    }

    private void UpdateLineVisual(
        ActiveLineVisual visual,
        LyricsLineSelection selection,
        int visibleLineCount,
        bool hasDuet,
        bool isMultiLine,
        bool wordMode,
        TimeSpan position)
    {
        var line = selection.Line;
        var fontSize = LyricsLayoutMetrics.GetActiveLineFontSize(
            LyricsText.FontSize,
            visibleLineCount,
            line.IsBackground);
        var opacity = line.IsBackground ? BackgroundActiveOpacity : 1.0;
        var textAlignment = selection.IsDuetSide
            ? TextAlignment.Right
            : hasDuet
                ? TextAlignment.Left
                : TextAlignment.Center;
        visual.TargetOpacity = opacity;

        if (visual.WordPresenter != null && wordMode && line.Words.Count > 0)
        {
            visual.WordPresenter.Line = line;
            visual.WordPresenter.Position = position;
            visual.WordPresenter.FontSize = fontSize;
            visual.WordPresenter.Foreground = LyricsText.Foreground;
            visual.WordPresenter.TextAlignment = textAlignment;
            visual.WordPresenter.Opacity = opacity;
            ApplyWordLyricsAnimationSettings(visual.WordPresenter);
            AutomationProperties.SetName(visual.WordPresenter, line.Text);
        }
        else if (visual.Control is TextBlock textBlock)
        {
            textBlock.Text = line.Text;
            textBlock.FontSize = fontSize;
            textBlock.Foreground = LyricsText.Foreground;
            textBlock.TextAlignment = textAlignment;
            textBlock.Opacity = opacity;
        }

        visual.Control.Margin = isMultiLine
            ? new Thickness(0, -1, 0, -1)
            : default;
    }

    private void RebuildInterludeVisual(LyricsInterlude interlude, TimeSpan position)
    {
        CompleteTransition();
        _activeLineVisuals.Clear();
        _backgroundFadeTargets.Clear();
        _lineMotions.Clear();
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
        _lineMotions.Clear();
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
        _lineMotions.Clear();
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
        _backgroundOnlyFade = false;
        _lineMotions.Clear();

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
        if (_lineMotions.Count > 0)
        {
            TickLineMotions();
            if (_lineMotions.Count == 0 && _transition == null && !_backgroundOnlyFade)
            {
                _transitionTimer.Stop();
            }

            if (_transition == null && !_backgroundOnlyFade)
            {
                return;
            }
        }

        if (_backgroundOnlyFade)
        {
            var backgroundElapsed = Environment.TickCount64 - _backgroundOnlyFadeStartedAt;
            var backgroundProgress = Math.Clamp(
                (backgroundElapsed - BackgroundTransitionDelayMs) / BackgroundTransitionDurationMs,
                0,
                1);
            var backgroundEased = backgroundProgress <= 0
                ? 0
                : 1 - Math.Pow(1 - backgroundProgress, 4);
            foreach (var (control, targetOpacity) in _backgroundFadeTargets)
            {
                control.Opacity = targetOpacity * backgroundEased;
            }

            if (backgroundProgress >= 1)
            {
                CompleteTransition();
            }

            return;
        }

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

        var fullBackgroundProgress = Math.Clamp(
            (elapsedMs - BackgroundTransitionDelayMs) / BackgroundTransitionDurationMs,
            0,
            1);
        var fullBackgroundEased = fullBackgroundProgress <= 0
            ? 0
            : 1 - Math.Pow(1 - fullBackgroundProgress, 4);
        foreach (var (control, targetOpacity) in _backgroundFadeTargets)
        {
            control.Opacity = targetOpacity * fullBackgroundEased;
        }

        if (progress >= 1 && fullBackgroundProgress >= 1)
        {
            CompleteTransition();
        }
    }

    private void TickLineMotions()
    {
        var now = Environment.TickCount64;
        for (var i = _lineMotions.Count - 1; i >= 0; i--)
        {
            var motion = _lineMotions[i];
            var progress = Math.Clamp(
                (now - motion.StartedAtTick - motion.DelayMs) / motion.DurationMs,
                0,
                1);
            var eased = progress <= 0
                ? 0
                : 1 - Math.Pow(1 - progress, motion.IsBackgroundStyle ? 4 : 3);
            motion.Control.Opacity = motion.FromOpacity + ((motion.ToOpacity - motion.FromOpacity) * eased);
            motion.Transform.Y = motion.FromY + ((motion.ToY - motion.FromY) * eased);
            if (progress < 1)
            {
                continue;
            }

            motion.Control.Opacity = motion.ToOpacity;
            motion.Transform.Y = motion.ToY;
            if (motion.RemoveWhenDone)
            {
                if (motion.RemoveFromExitLayer)
                {
                    _exitLayer.Children.Remove(motion.Control);
                }
                else
                {
                    _front.Children.Remove(motion.Control);
                    _back.Children.Remove(motion.Control);
                }
            }

            _lineMotions.RemoveAt(i);
        }
    }

    private void FinishLineMotions(bool removeExiting, bool settleRemaining)
    {
        foreach (var motion in _lineMotions)
        {
            if (motion.RemoveWhenDone && removeExiting)
            {
                if (motion.RemoveFromExitLayer)
                {
                    _exitLayer.Children.Remove(motion.Control);
                }
                else
                {
                    _front.Children.Remove(motion.Control);
                    _back.Children.Remove(motion.Control);
                }

                continue;
            }

            if (settleRemaining)
            {
                motion.Control.Opacity = motion.ToOpacity;
                motion.Transform.Y = motion.ToY;
            }
        }

        _lineMotions.Clear();
    }

    private static TranslateTransform EnsureLineTransform(Control control)
    {
        if (control.RenderTransform is TranslateTransform existing)
        {
            return existing;
        }

        var transform = new TranslateTransform();
        control.RenderTransform = transform;
        return transform;
    }

    private void StartBackgroundOnlyFade()
    {
        _backgroundOnlyFade = true;
        _backgroundOnlyFadeStartedAt = Environment.TickCount64;
        _transition = null;
        _transitionTimer.Start();
    }

    private void CompleteFullFrameTransitionOnly()
    {
        _transitionTimer.Stop();
        _transition = null;
        _backgroundOnlyFade = false;
        _front.IsVisible = true;
        _front.Opacity = 1;
        ((TranslateTransform)_front.RenderTransform!).Y = 0;
        SettleBackgroundFadeTargets();
        _backgroundFadeTargets.Clear();
        _back.Children.Clear();
        _back.IsVisible = false;
        _back.Opacity = 0;
        ((TranslateTransform)_back.RenderTransform!).Y = 0;
    }

    private void CompleteTransition()
    {
        FinishLineMotions(removeExiting: true, settleRemaining: true);
        CompleteFullFrameTransitionOnly();
        ClearExitLayer();
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
                      _exitLayer.Children.Count > 0 ||
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

    private sealed class ActiveLineVisual
    {
        public ActiveLineVisual(
            int lineIndex,
            Control control,
            WordLyricsPresenter? wordPresenter,
            double targetOpacity,
            bool isBackground)
        {
            LineIndex = lineIndex;
            Control = control;
            WordPresenter = wordPresenter;
            TargetOpacity = targetOpacity;
            IsBackground = isBackground;
        }

        public int LineIndex { get; }
        public Control Control { get; }
        public WordLyricsPresenter? WordPresenter { get; }
        public double TargetOpacity { get; set; }
        public bool IsBackground { get; }
    }

    private sealed record LineFirstFrame(
        double Y,
        double Height,
        double Opacity,
        double TargetOpacity,
        double FontSize);

    private sealed record PlannedLineState(
        int LineIndex,
        int Order,
        LyricsLineSelection Selection,
        double TargetFontSize,
        double TargetOpacity,
        TextAlignment TextAlignment,
        bool IsMultiLine,
        bool IsBackground);

    private sealed record LineMotion(
        Control Control,
        double FromOpacity,
        double ToOpacity,
        double FromY,
        double ToY,
        double DelayMs,
        double DurationMs,
        bool RemoveWhenDone,
        long StartedAtTick,
        TranslateTransform Transform,
        bool IsBackgroundStyle,
        bool RemoveFromExitLayer = false);

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
