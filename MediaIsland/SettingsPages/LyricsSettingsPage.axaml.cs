using System.Net.Http;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ClassIsland.Core.Attributes;
using MediaIsland.Helpers;
using MediaIsland.Models;
using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Lyrics.Models;
using MediaIsland.Services.Media;
using MediaIsland.Services.MediaLink;

namespace MediaIsland.SettingsPages;

/// <summary>「歌词」页：来源配置与针对当前曲目的固定、候选操作。</summary>
[SettingsPageInfo("mediaisland.lyrics", "歌词", "\uF307", "\uF306")]
[Group(GroupId)]
public partial class LyricsSettingsPage : MediaIslandSettingsPage
{
    private readonly IMediaService _mediaService;
    private readonly LyricsSearchService _lyricsSearchService;
    private readonly IEffectiveMediaSource? _effectiveMediaSource;

    private string _amllApiBaseUrl = string.Empty;
    private string _amllConnectionStatus = "留空表示不使用 AMLL 来源。";
    private string _sPlayerNextApiBaseUrl = LyricsSourceSettings.DefaultSPlayerNextApiBaseUrl;
    private string _sPlayerNextConnectionStatus = "默认 http://127.0.0.1:14558。";
    private string _lyricsCacheStatus = string.Empty;
    private string _currentLyricsSourceDisplay = "当前使用：暂无";
    private string _currentLyricsCandidatesStatus = "播放媒体后将在此处显示已启用歌词源的搜索候选。";
    private string _currentLyricsPinStatus = "播放媒体后将在此处显示固定状态。";
    private CancellationTokenSource? _lyricsCandidatesCancellation;
    private CancellationTokenSource? _lyricsCandidateApplyCancellation;
    private long _lyricsCandidatesSearchVersion;
    private bool _suppressLyricsSave;

    public ObservableCollection<LyricsSourceItemViewModel> LyricsSourceItems { get; } = [];

    public ObservableCollection<LyricsCandidateItemViewModel> LyricsCandidateItems { get; } = [];

    public string AmllApiBaseUrl
    {
        get => _amllApiBaseUrl;
        set
        {
            if (SetProperty(ref _amllApiBaseUrl, value))
            {
                PersistLyricsSettings();
            }
        }
    }

    public string AmllConnectionStatus
    {
        get => _amllConnectionStatus;
        private set => SetProperty(ref _amllConnectionStatus, value);
    }

    public string SPlayerNextApiBaseUrl
    {
        get => _sPlayerNextApiBaseUrl;
        set
        {
            if (SetProperty(ref _sPlayerNextApiBaseUrl, value))
            {
                PersistLyricsSettings();
            }
        }
    }

    public string SPlayerNextConnectionStatus
    {
        get => _sPlayerNextConnectionStatus;
        private set => SetProperty(ref _sPlayerNextConnectionStatus, value);
    }

    /// <summary>清空缓存后的反馈；为空时不显示。</summary>
    public string LyricsCacheStatus
    {
        get => _lyricsCacheStatus;
        private set
        {
            if (SetProperty(ref _lyricsCacheStatus, value))
            {
                OnPropertyChanged(nameof(HasLyricsCacheStatus));
            }
        }
    }

    public bool HasLyricsCacheStatus => !string.IsNullOrEmpty(_lyricsCacheStatus);

    public string CurrentLyricsSourceDisplay
    {
        get => _currentLyricsSourceDisplay;
        private set => SetProperty(ref _currentLyricsSourceDisplay, value);
    }

    public string CurrentLyricsCandidatesStatus
    {
        get => _currentLyricsCandidatesStatus;
        private set => SetProperty(ref _currentLyricsCandidatesStatus, value);
    }

    public string CurrentLyricsPinStatus
    {
        get => _currentLyricsPinStatus;
        private set => SetProperty(ref _currentLyricsPinStatus, value);
    }

    public LyricsSettingsPage(
        Plugin plugin,
        IMediaService mediaService,
        LyricsSearchService lyricsSearchService,
        IEffectiveMediaSource? effectiveMediaSource = null) : base(plugin)
    {
        _mediaService = mediaService;
        _lyricsSearchService = lyricsSearchService;
        _effectiveMediaSource = effectiveMediaSource;
        InitializeComponent();
        LoadLyricsSettings();
        if (_effectiveMediaSource is not null)
        {
            _effectiveMediaSource.EffectiveMediaChanged += OnMediaInfoChanged;
            _effectiveMediaSource.EffectiveLyricsChanged += OnEffectiveLyricsChanged;
        }
        else
        {
            _mediaService.MediaInfoChanged += OnMediaInfoChanged;
            _lyricsSearchService.CurrentResultChanged += OnCurrentResultChanged;
        }

        UpdateCurrentLyricsSource(CurrentUiLyrics);
        _ = RefreshLyricsCandidatesAsync(_mediaService.CurrentMediaInfo);
        StartMediaServiceAsync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_effectiveMediaSource is not null)
        {
            _effectiveMediaSource.EffectiveMediaChanged -= OnMediaInfoChanged;
            _effectiveMediaSource.EffectiveLyricsChanged -= OnEffectiveLyricsChanged;
        }

        _mediaService.MediaInfoChanged -= OnMediaInfoChanged;
        _lyricsSearchService.CurrentResultChanged -= OnCurrentResultChanged;
        CancelLyricsCandidateSearch();
        CancelLyricsCandidateApply();
    }

    private LyricsSearchResult? CurrentUiLyrics =>
        _effectiveMediaSource.GetCurrentUiLyrics(_lyricsSearchService, _mediaService);

    private async void StartMediaServiceAsync()
    {
        try
        {
            await _mediaService.EnsureStartedAsync();
            await RefreshLyricsCandidatesAsync(_mediaService.CurrentMediaInfo);
        }
        catch
        {
            // Media source discovery is best-effort on the settings page.
        }
    }

    private void OnMediaInfoChanged(object? sender, MediaInfoChangedEventArgs e)
    {
        if (e.ChangeKind is not (MediaInfoChangeKind.CurrentSession or MediaInfoChangeKind.MediaProperties))
        {
            return;
        }

        // 有协调器时经它读，纯接收端上注入歌词才到得了这一行；
        // 缺席时保持旧读法，用事件里的媒体避免与 CurrentMediaInfo 的时序差。
        Dispatcher.UIThread.Post(() => UpdateCurrentLyricsSource(
            _effectiveMediaSource is null
                ? _lyricsSearchService.GetCurrentResultFor(e.MediaInfo)
                : CurrentUiLyrics));
        CancelLyricsCandidateApply();
        _ = RefreshLyricsCandidatesAsync(e.MediaInfo);
    }

    private void OnCurrentResultChanged(object? sender, LyricsSearchResultChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() => UpdateCurrentLyricsSource(
            _lyricsSearchService.GetCurrentResultFor(_mediaService.CurrentMediaInfo)));
    }

    private void OnEffectiveLyricsChanged(object? sender, LyricsSearchResultChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() => UpdateCurrentLyricsSource(CurrentUiLyrics));
    }

    private void UpdateCurrentLyricsSource(LyricsSearchResult? result)
    {
        if (result is null)
        {
            CurrentLyricsSourceDisplay = "当前使用：暂无";
            return;
        }

        // 外部注入时 Source 恒为 External，看不出歌词实际来自哪；
        // 上游标注了真实来源就一并显示，例如「其他设备（QQ 音乐）」。
        var name = LyricsSourceItemViewModel.GetDisplayName(result.Source);
        if (result.Source == LyricsSourceId.External &&
            result.OriginSource is { } origin &&
            origin != LyricsSourceId.External)
        {
            name = $"{name}（{LyricsSourceItemViewModel.GetDisplayName(origin)}）";
        }

        CurrentLyricsSourceDisplay = $"当前使用：{name}";
    }

    private async Task RefreshLyricsCandidatesAsync(MediaInfo? info)
    {
        _ = RefreshLyricsPinStatusAsync(info);

        var searchVersion = Interlocked.Increment(ref _lyricsCandidatesSearchVersion);
        CancelLyricsCandidateSearch();

        if (info == null || string.IsNullOrWhiteSpace(info.Title))
        {
            await PostToUiAsync(() => ClearLyricsCandidates("播放媒体后将在此处显示已启用歌词源的搜索候选。"));
            return;
        }

        if (!MediaSourceFilter.IsEnabled(info.SourceApp, Settings.MediaSourceList))
        {
            await PostToUiAsync(() => ClearLyricsCandidates("当前媒体来源已禁用，不搜索歌词候选。"));
            return;
        }

        if (!MediaSourceFilter.IsLyricsSearchEnabled(info.SourceApp, Settings.MediaSourceList) &&
            !SPlayerNextMediaSource.Matches(info.SourceApp))
        {
            await PostToUiAsync(() => ClearLyricsCandidates("当前媒体来源已禁用歌词搜索，不搜索歌词候选。"));
            return;
        }

        if (SPlayerNextMediaSource.Matches(info.SourceApp) &&
            !MediaSourceFilter.IsLyricsSearchEnabled(info.SourceApp, Settings.MediaSourceList))
        {
            await PostToUiAsync(() =>
                ClearLyricsCandidates("当前为 SPlayer-Next：优先使用其外部 API 歌词；通用歌词搜索默认关闭。"));
            return;
        }

        if (!Settings.Lyrics.Sources.Any(source => source.IsEnabled))
        {
            await PostToUiAsync(() => ClearLyricsCandidates("没有启用的歌词来源。"));
            return;
        }

        var cancellation = new CancellationTokenSource();
        _lyricsCandidatesCancellation = cancellation;
        await PostToUiAsync(() => ClearLyricsCandidates("正在搜索已启用歌词源的候选..."));

        try
        {
            var candidates = await _lyricsSearchService.SearchCandidatesAsync(info, cancellation.Token);
            if (IsDetached || cancellation.IsCancellationRequested ||
                searchVersion != Volatile.Read(ref _lyricsCandidatesSearchVersion))
            {
                return;
            }

            await PostToUiAsync(() =>
            {
                LyricsCandidateItems.Clear();
                foreach (var candidate in candidates)
                {
                    LyricsCandidateItems.Add(new LyricsCandidateItemViewModel(candidate));
                }

                CurrentLyricsCandidatesStatus = candidates.Count == 0
                    ? "各已启用歌词源未返回歌词候选。"
                    : $"已找到 {candidates.Count} 个候选，按得分从高到低排序。";
            });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A newer media event or page unload superseded this search.
        }
        catch
        {
            if (!IsDetached && searchVersion == Volatile.Read(ref _lyricsCandidatesSearchVersion))
            {
                await PostToUiAsync(() => ClearLyricsCandidates("搜索歌词候选时发生错误。"));
            }
        }
        finally
        {
            if (ReferenceEquals(_lyricsCandidatesCancellation, cancellation))
            {
                _lyricsCandidatesCancellation = null;
                cancellation.Dispose();
            }
        }
    }

    private void RefreshLyricsCandidatesOnClick(object? sender, RoutedEventArgs e)
    {
        _ = RefreshLyricsCandidatesAsync(_mediaService.CurrentMediaInfo);
    }

    private async void ApplyLyricsCandidateOnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Control { DataContext: LyricsCandidateItemViewModel item } ||
            _mediaService.CurrentMediaInfo is not { } mediaInfo)
        {
            return;
        }

        if (!MediaSourceFilter.IsEnabled(mediaInfo.SourceApp, Settings.MediaSourceList))
        {
            CurrentLyricsCandidatesStatus = "当前媒体来源已禁用，不能应用歌词候选。";
            return;
        }

        if (!MediaSourceFilter.IsLyricsSearchEnabled(mediaInfo.SourceApp, Settings.MediaSourceList) &&
            !SPlayerNextMediaSource.Matches(mediaInfo.SourceApp))
        {
            CurrentLyricsCandidatesStatus = "当前媒体来源已禁用歌词搜索，不能应用歌词候选。";
            return;
        }

        CancelLyricsCandidateApply();
        var cancellation = new CancellationTokenSource();
        _lyricsCandidateApplyCancellation = cancellation;
        CurrentLyricsCandidatesStatus = $"正在应用 {item.Source} 的歌词候选...";

        try
        {
            var result = await _lyricsSearchService.ApplyCandidateAsync(
                mediaInfo,
                item.Candidate,
                cancellation.Token);
            if (IsDetached || cancellation.IsCancellationRequested)
            {
                return;
            }

            CurrentLyricsCandidatesStatus = result == null
                ? "未能应用所选歌词候选。"
                : $"已应用：{item.Source} - {item.Title}（评分 {item.Score}）。";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A newer media event or selection superseded this request.
        }
        catch
        {
            if (!IsDetached)
            {
                CurrentLyricsCandidatesStatus = "应用所选歌词候选时发生错误。";
            }
        }
        finally
        {
            if (ReferenceEquals(_lyricsCandidateApplyCancellation, cancellation))
            {
                _lyricsCandidateApplyCancellation = null;
                cancellation.Dispose();
            }
        }
    }

    private async void PinCurrentLyricsOnClick(object? sender, RoutedEventArgs e)
    {
        if (_mediaService.CurrentMediaInfo is not { } mediaInfo)
        {
            return;
        }

        CurrentLyricsPinStatus = "正在固定当前歌词...";
        var pinned = await _lyricsSearchService.PinCurrentResultAsync(mediaInfo);
        if (IsDetached)
        {
            return;
        }

        if (pinned == null)
        {
            CurrentLyricsPinStatus = _lyricsSearchService.LastPinError ?? "固定失败：当前没有可固定的歌词。";
            return;
        }

        await RefreshLyricsPinStatusAsync(mediaInfo);
    }

    private async void ImportLyricsFileOnClick(object? sender, RoutedEventArgs e)
    {
        if (_mediaService.CurrentMediaInfo is not { } mediaInfo ||
            TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择歌词文件",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("歌词文件")
                {
                    Patterns = ["*.lrc", "*.qrc", "*.krc", "*.ttml"]
                }
            ]
        });

        var filePath = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        CurrentLyricsPinStatus = "正在导入歌词文件...";
        var imported = await _lyricsSearchService.PinFromFileAsync(mediaInfo, filePath);
        if (IsDetached)
        {
            return;
        }

        if (imported == null)
        {
            CurrentLyricsPinStatus = _lyricsSearchService.LastPinError ?? "导入失败：无法解析所选歌词文件。";
            return;
        }

        await RefreshLyricsPinStatusAsync(mediaInfo);
    }

    private async void UnpinLyricsOnClick(object? sender, RoutedEventArgs e)
    {
        if (_mediaService.CurrentMediaInfo is not { } mediaInfo)
        {
            return;
        }

        var removed = await _lyricsSearchService.UnpinAsync(mediaInfo);
        if (IsDetached)
        {
            return;
        }

        if (!removed)
        {
            CurrentLyricsPinStatus = "当前曲目没有固定的歌词。";
            return;
        }

        await RefreshLyricsPinStatusAsync(mediaInfo);
    }

    private async void ClearLyricsCacheOnClick(object? sender, RoutedEventArgs e)
    {
        await _lyricsSearchService.ClearPersistentCacheAsync();
        if (IsDetached)
        {
            return;
        }

        // 固定歌词不受清空缓存影响，说明这点免得用户以为固定也被清了。
        LyricsCacheStatus = "已清空歌词缓存（固定的歌词保留）。";
    }

    private async Task RefreshLyricsPinStatusAsync(MediaInfo? mediaInfo)
    {
        if (mediaInfo is not { } info || string.IsNullOrWhiteSpace(info.Title))
        {
            CurrentLyricsPinStatus = "播放媒体后将在此处显示固定状态。";
            return;
        }

        var pin = await _lyricsSearchService.GetPinAsync(info);
        if (IsDetached)
        {
            return;
        }

        var isSearchDisabled =
            !MediaSourceFilter.IsLyricsSearchEnabled(info.SourceApp, Settings.MediaSourceList) &&
            !SPlayerNextMediaSource.Matches(info.SourceApp);

        CurrentLyricsPinStatus = LyricsPinStatusText.Describe(
            pin != null,
            pin?.Source.ToString(),
            pin?.SavedAtUtc,
            SPlayerNextMediaSource.Matches(info.SourceApp),
            isSearchDisabled);
    }

    private void CancelLyricsCandidateSearch()
    {
        var cancellation = Interlocked.Exchange(ref _lyricsCandidatesCancellation, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private void CancelLyricsCandidateApply()
    {
        var cancellation = Interlocked.Exchange(ref _lyricsCandidateApplyCancellation, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private void ClearLyricsCandidates(string status)
    {
        LyricsCandidateItems.Clear();
        CurrentLyricsCandidatesStatus = status;
    }

    private void LoadLyricsSettings()
    {
        _suppressLyricsSave = true;
        Settings.Lyrics = LyricsSourceSettings.Normalize(Settings.Lyrics);
        LyricsSourceItems.Clear();
        foreach (var source in Settings.Lyrics.Sources)
        {
            LyricsSourceItems.Add(new LyricsSourceItemViewModel(source, PersistLyricsSettings));
        }

        _amllApiBaseUrl = Settings.Lyrics.AmllApiBaseUrl;
        OnPropertyChanged(nameof(AmllApiBaseUrl));
        _sPlayerNextApiBaseUrl = Settings.Lyrics.SPlayerNextApiBaseUrl;
        OnPropertyChanged(nameof(SPlayerNextApiBaseUrl));
        _suppressLyricsSave = false;
    }

    private void PersistLyricsSettings()
    {
        if (_suppressLyricsSave)
        {
            return;
        }

        Settings.Lyrics = LyricsSourceSettings.Normalize(new LyricsSourceSettings
        {
            AmllApiBaseUrl = LyricsSourceSettings.NormalizeAmllBaseUrl(AmllApiBaseUrl),
            SPlayerNextApiBaseUrl = LyricsSourceSettings.NormalizeSPlayerNextBaseUrl(SPlayerNextApiBaseUrl),
            Sources = LyricsSourceItems.Select(item => new LyricsSourceEntry
            {
                Id = item.Id,
                IsEnabled = item.IsEnabled,
                UseWordSyncedLyrics = item.UseWordSyncedLyrics,
                GlobalOffsetMilliseconds = item.GlobalOffsetMilliseconds
            }).ToList()
        });
        SaveSettings();
    }

    private void MoveLyricsSourceUpOnClick(object? sender, RoutedEventArgs e)
    {
        MoveLyricsSource(sender, -1);
    }

    private void MoveLyricsSourceDownOnClick(object? sender, RoutedEventArgs e)
    {
        MoveLyricsSource(sender, 1);
    }

    private void MoveLyricsSource(object? sender, int delta)
    {
        if (sender is not Button { Tag: LyricsSourceItemViewModel item })
        {
            return;
        }

        var index = LyricsSourceItems.IndexOf(item);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= LyricsSourceItems.Count)
        {
            return;
        }

        LyricsSourceItems.Move(index, target);
        PersistLyricsSettings();
    }

    private async void TestAmllConnectionOnClick(object? sender, RoutedEventArgs e)
    {
        var baseUrl = LyricsSourceSettings.NormalizeAmllBaseUrl(AmllApiBaseUrl);
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            AmllConnectionStatus = "请先填写有效的 http(s) API 地址。";
            return;
        }

        AmllConnectionStatus = "正在测试连接...";
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            var url = $"{baseUrl}/api/v1/lyrics/search?musicName=ME!&artistName=Taylor%20Swift";
            using var response = await client.GetAsync(url);
            AmllConnectionStatus = response.IsSuccessStatusCode
                ? $"连接成功：HTTP {(int)response.StatusCode}"
                : $"连接失败：HTTP {(int)response.StatusCode}";
        }
        catch (Exception ex)
        {
            AmllConnectionStatus = $"连接失败：{ex.Message}";
        }
    }

    private async void TestSPlayerNextConnectionOnClick(object? sender, RoutedEventArgs e)
    {
        var baseUrl = LyricsSourceSettings.NormalizeSPlayerNextBaseUrl(SPlayerNextApiBaseUrl);
        SPlayerNextConnectionStatus = "正在测试连接...";
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            using var response = await client.GetAsync($"{baseUrl}/api/info");
            if (!response.IsSuccessStatusCode)
            {
                SPlayerNextConnectionStatus = $"连接失败：HTTP {(int)response.StatusCode}";
                return;
            }

            var body = await response.Content.ReadAsStringAsync();
            SPlayerNextConnectionStatus = string.IsNullOrWhiteSpace(body)
                ? $"连接成功：HTTP {(int)response.StatusCode}"
                : $"连接成功：{body.Trim()}";
        }
        catch (Exception ex)
        {
            SPlayerNextConnectionStatus = $"连接失败：{ex.Message}";
        }
    }
}
