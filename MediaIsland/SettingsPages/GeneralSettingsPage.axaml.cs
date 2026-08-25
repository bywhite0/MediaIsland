using System.IO;
using System.Collections.ObjectModel;
using System.Net.Http;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Controls;
using ClassIsland.Core.Enums.SettingsWindow;
using ClassIsland.Shared.Helpers;
using MediaIsland.Helpers;
using MediaIsland.Models;
using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Lyrics.Models;
using MediaIsland.Services.Media;
using MediaIsland.Services.Media.SourceDisplay;
using MediaIsland.Services.MediaLink;

namespace MediaIsland.SettingsPages
{
    /// <summary>
    /// GeneralSettingsPage.xaml 的交互逻辑
    /// </summary>
    [SettingsPageInfo(
        "mediaisland.general",
        "MediaIsland",
        "\uEBCA",
        "\uEBC9",
        SettingsPageCategory.External)]
    public partial class GeneralSettingsPage : SettingsPageBase, INotifyPropertyChanged
    {
        public Plugin Plugin { get; }
        public PluginSettings Settings { get; }
        private readonly IMediaService _mediaService;
        private readonly IMediaSourceDisplayService _mediaSourceDisplayService;
        private readonly LyricsSearchService _lyricsSearchService;
        private readonly IMediaLinkGateway? _mediaLinkGateway;
        private readonly IEffectiveMediaSource? _effectiveMediaSource;
        private readonly MediaLinkInjectionStore? _injectionStore;
        private readonly MediaLinkUpstreamHostedService? _upstream;
        private string _mediaLinkStatusText = "未启用";
        private string _mediaLinkExposureWarning = string.Empty;
        private string _mediaLinkUpstreamStatusText = "未启用";
        private string _mediaLinkConfigCodeHint = string.Empty;
        private string _mediaLinkUpstreamCodeHint = string.Empty;
        private bool _isDetached;
        private string _currentMediaTitle = "未检测到正在播放的媒体";
        private string _currentMediaArtistAlbum = "播放媒体后会在此处显示标题、艺术家、专辑与进度。";
        private string _currentMediaPlaybackStatus = "无媒体";
        private string _currentMediaStatusGlyph = "\uE9CE";
        private string _currentMediaTimeline = "00:00 / 00:00";
        private string _currentMediaSourceDisplay = "播放源：-";
        private string _currentMediaSourceId = "播放源 ID：-";
        private string _currentMediaSourceIconStatus = "图标状态：无图标";
        private Bitmap? _currentMediaThumbnail;
        private Bitmap? _currentMediaSourceIcon;
        private string _amllApiBaseUrl = string.Empty;
        private string _amllConnectionStatus = "留空表示不使用 AMLL 来源。";
        private string _sPlayerNextApiBaseUrl = LyricsSourceSettings.DefaultSPlayerNextApiBaseUrl;
        private string _sPlayerNextConnectionStatus = "默认 http://127.0.0.1:14558；播放源为 SPlayer-Next 时优先使用其外部 API 歌词。";
        private string _currentLyricsSourceDisplay = "当前使用：暂无";
        private string _currentLyricsCandidatesStatus = "播放媒体后将在此处显示已启用歌词源的搜索候选。";
        private string _currentLyricsPinStatus = "播放媒体后将在此处显示固定状态。";
        private CancellationTokenSource? _lyricsCandidatesCancellation;
        private CancellationTokenSource? _lyricsCandidateApplyCancellation;
        private long _lyricsCandidatesSearchVersion;
        private bool _suppressLyricsSave;

        private event PropertyChangedEventHandler? NotifyPropertyChanged;

        event PropertyChangedEventHandler? INotifyPropertyChanged.PropertyChanged
        {
            add => NotifyPropertyChanged += value;
            remove => NotifyPropertyChanged -= value;
        }

        public string CurrentMediaTitle
        {
            get => _currentMediaTitle;
            private set => SetProperty(ref _currentMediaTitle, value);
        }

        public string CurrentMediaArtistAlbum
        {
            get => _currentMediaArtistAlbum;
            private set => SetProperty(ref _currentMediaArtistAlbum, value);
        }

        public string CurrentMediaPlaybackStatus
        {
            get => _currentMediaPlaybackStatus;
            private set => SetProperty(ref _currentMediaPlaybackStatus, value);
        }

        public string CurrentMediaStatusGlyph
        {
            get => _currentMediaStatusGlyph;
            private set => SetProperty(ref _currentMediaStatusGlyph, value);
        }

        public string CurrentMediaTimeline
        {
            get => _currentMediaTimeline;
            private set => SetProperty(ref _currentMediaTimeline, value);
        }

        public string CurrentMediaSourceDisplay
        {
            get => _currentMediaSourceDisplay;
            private set => SetProperty(ref _currentMediaSourceDisplay, value);
        }

        public string CurrentMediaSourceId
        {
            get => _currentMediaSourceId;
            private set => SetProperty(ref _currentMediaSourceId, value);
        }

        public string CurrentMediaSourceIconStatus
        {
            get => _currentMediaSourceIconStatus;
            private set => SetProperty(ref _currentMediaSourceIconStatus, value);
        }

        public Bitmap? CurrentMediaThumbnail
        {
            get => _currentMediaThumbnail;
            private set
            {
                if (!SetProperty(ref _currentMediaThumbnail, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(HasCurrentMediaThumbnail));
            }
        }

        public bool HasCurrentMediaThumbnail => CurrentMediaThumbnail != null;

        public Bitmap? CurrentMediaSourceIcon
        {
            get => _currentMediaSourceIcon;
            private set
            {
                if (!SetProperty(ref _currentMediaSourceIcon, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(HasCurrentMediaSourceIcon));
            }
        }

        public bool HasCurrentMediaSourceIcon => CurrentMediaSourceIcon != null;

        public ObservableCollection<LyricsSourceItemViewModel> LyricsSourceItems { get; } = [];

        public ObservableCollection<LyricsCandidateItemViewModel> LyricsCandidateItems { get; } = [];

        public string AmllApiBaseUrl
        {
            get => _amllApiBaseUrl;
            set
            {
                if (!SetProperty(ref _amllApiBaseUrl, value))
                {
                    return;
                }

                PersistLyricsSettings();
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
                if (!SetProperty(ref _sPlayerNextApiBaseUrl, value))
                {
                    return;
                }

                PersistLyricsSettings();
            }
        }

        public string SPlayerNextConnectionStatus
        {
            get => _sPlayerNextConnectionStatus;
            private set => SetProperty(ref _sPlayerNextConnectionStatus, value);
        }

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

        public string MediaLinkStatusText
        {
            get => _mediaLinkStatusText;
            private set => SetProperty(ref _mediaLinkStatusText, value);
        }

        /// <summary>非回环监听时的明文暴露提示；为空表示无需提示。</summary>
        public string MediaLinkExposureWarning
        {
            get => _mediaLinkExposureWarning;
            private set
            {
                if (SetProperty(ref _mediaLinkExposureWarning, value))
                {
                    OnPropertyChanged(nameof(HasMediaLinkExposureWarning));
                }
            }
        }

        public bool HasMediaLinkExposureWarning => !string.IsNullOrEmpty(_mediaLinkExposureWarning);

        public string MediaLinkUpstreamStatusText
        {
            get => _mediaLinkUpstreamStatusText;
            private set => SetProperty(ref _mediaLinkUpstreamStatusText, value);
        }

        /// <summary>复制配置码后的反馈；为空时不显示。</summary>
        public string MediaLinkConfigCodeHint
        {
            get => _mediaLinkConfigCodeHint;
            private set
            {
                if (SetProperty(ref _mediaLinkConfigCodeHint, value))
                {
                    OnPropertyChanged(nameof(HasMediaLinkConfigCodeHint));
                }
            }
        }

        public bool HasMediaLinkConfigCodeHint => !string.IsNullOrEmpty(_mediaLinkConfigCodeHint);

        /// <summary>粘贴配置码后的反馈；为空时不显示。</summary>
        public string MediaLinkUpstreamCodeHint
        {
            get => _mediaLinkUpstreamCodeHint;
            private set
            {
                if (SetProperty(ref _mediaLinkUpstreamCodeHint, value))
                {
                    OnPropertyChanged(nameof(HasMediaLinkUpstreamCodeHint));
                }
            }
        }

        public bool HasMediaLinkUpstreamCodeHint => !string.IsNullOrEmpty(_mediaLinkUpstreamCodeHint);

        public int MediaLinkMediaSourceModeIndex
        {
            get => (int)Settings.MediaLinkMediaSourceMode;
            set
            {
                var normalized = value is < 0 or > 2 ? 0 : value;
                var mode = (MediaLinkMediaSourceMode)normalized;
                if (Settings.MediaLinkMediaSourceMode == mode)
                {
                    return;
                }

                Settings.MediaLinkMediaSourceMode = mode;
                OnPropertyChanged();
            }
        }


        public GeneralSettingsPage(
            Plugin plugin,
            IMediaService mediaService,
            IMediaSourceDisplayService mediaSourceDisplayService,
            LyricsSearchService lyricsSearchService,
            IMediaLinkGateway? mediaLinkGateway = null,
            IEffectiveMediaSource? effectiveMediaSource = null,
            MediaLinkInjectionStore? injectionStore = null,
            MediaLinkUpstreamHostedService? upstream = null)
        {
            Plugin = plugin;
            Settings = Plugin.Settings;
            _mediaService = mediaService;
            _mediaSourceDisplayService = mediaSourceDisplayService;
            _lyricsSearchService = lyricsSearchService;
            _mediaLinkGateway = mediaLinkGateway;
            _effectiveMediaSource = effectiveMediaSource;
            _injectionStore = injectionStore;
            _upstream = upstream;
            RemoveNullMediaSources();
            InitializeComponent();
            LoadLyricsSettings();
            DetachedFromVisualTree += (_, _) =>
            {
                _isDetached = true;
                UnsubscribeMediaLinkGateway();
                UnsubscribeUpstream();
                Settings.PropertyChanged -= OnPluginSettingsChangedForMediaLink;
                if (_effectiveMediaSource is not null)
                {
                    _effectiveMediaSource.EffectiveMediaChanged -= EffectiveMediaSource_OnMediaInfoChanged;
                }

                _mediaService.MediaInfoChanged -= MediaService_OnMediaInfoChanged;
                _lyricsSearchService.CurrentResultChanged -= LyricsSearchService_OnCurrentResultChanged;
                CancelLyricsCandidateSearch();
                CancelLyricsCandidateApply();
            };
            Settings.needRestart += RequestRestart;
            Settings.PropertyChanged += OnPluginSettingsChangedForMediaLink;
            if (_effectiveMediaSource is not null)
            {
                _effectiveMediaSource.EffectiveMediaChanged += EffectiveMediaSource_OnMediaInfoChanged;
            }
            else
            {
                _mediaService.MediaInfoChanged += MediaService_OnMediaInfoChanged;
            }
            _lyricsSearchService.CurrentResultChanged += LyricsSearchService_OnCurrentResultChanged;
            UpdateCurrentLyricsSource(_lyricsSearchService.GetCurrentResultFor(_mediaService.CurrentMediaInfo));
            _ = RefreshLyricsCandidatesAsync(_mediaService.CurrentMediaInfo);
            StartMediaServiceAsync();
            AddCurrentMediaSourceIfAvailable();
            _ = RefreshCurrentMediaInfoAsync(CurrentUiMediaInfo);
            RefreshMediaSourceDisplayInfos();
            RefreshMediaLinkStatus();
            SubscribeMediaLinkGateway();
            RefreshUpstreamStatus();
            SubscribeUpstream();
            var screenshotApp = new MediaSource
            {
                Source = "Microsoft.ScreenSketch_8wekyb3d8bbwe!App",
                IsEnabled = false
            };
            if (!Settings.MediaSourceList.Any(source => source?.Source == "Microsoft.ScreenSketch_8wekyb3d8bbwe!App"))
            {
                Settings.MediaSourceList.Add(screenshotApp);
                SaveMediaSourceSettings();
            }
        }

        private async void StartMediaServiceAsync()
        {
            try
            {
                await _mediaService.EnsureStartedAsync();
                AddCurrentMediaSourceIfAvailable();
                await RefreshCurrentMediaInfoAsync(CurrentUiMediaInfo);
                await RefreshLyricsCandidatesAsync(_mediaService.CurrentMediaInfo);
            }
            catch
            {
                // Media source discovery is best-effort on the settings page.
            }
        }


        private void EffectiveMediaSource_OnMediaInfoChanged(object? sender, MediaInfoChangedEventArgs e)
        {
            MediaService_OnMediaInfoChanged(sender, e);
        }

        private MediaInfo? CurrentUiMediaInfo =>
            _effectiveMediaSource.GetCurrentUiMediaInfo(_mediaService);

        private void MediaService_OnMediaInfoChanged(object? sender, MediaInfoChangedEventArgs e)
        {
            if (e.ChangeKind is MediaInfoChangeKind.CurrentSession or MediaInfoChangeKind.MediaProperties)
            {
                Dispatcher.UIThread.Post(() => UpdateCurrentLyricsSource(
                    _lyricsSearchService.GetCurrentResultFor(e.MediaInfo)));
                CancelLyricsCandidateApply();
                _ = RefreshLyricsCandidatesAsync(e.MediaInfo);
            }

            _ = RefreshCurrentMediaInfoAsync(e.MediaInfo);
            if (e.MediaInfo == null)
            {
                return;
            }

            Dispatcher.UIThread.Post(() => AddMediaSource(e.MediaInfo.SourceApp));
        }

        private void LyricsSearchService_OnCurrentResultChanged(
            object? sender,
            LyricsSearchResultChangedEventArgs e)
        {
            Dispatcher.UIThread.Post(() => UpdateCurrentLyricsSource(
                _lyricsSearchService.GetCurrentResultFor(_mediaService.CurrentMediaInfo)));
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
                await UpdateCurrentMediaUiAsync(() => ClearLyricsCandidates("播放媒体后将在此处显示已启用歌词源的搜索候选。"));
                return;
            }

            if (!MediaSourceFilter.IsEnabled(info.SourceApp, Settings.MediaSourceList))
            {
                await UpdateCurrentMediaUiAsync(() => ClearLyricsCandidates("当前媒体来源已禁用，不搜索歌词候选。"));
                return;
            }

            if (!MediaSourceFilter.IsLyricsSearchEnabled(info.SourceApp, Settings.MediaSourceList) &&
                !SPlayerNextMediaSource.Matches(info.SourceApp))
            {
                await UpdateCurrentMediaUiAsync(() => ClearLyricsCandidates("当前媒体来源已禁用歌词搜索，不搜索歌词候选。"));
                return;
            }

            if (SPlayerNextMediaSource.Matches(info.SourceApp) &&
                !MediaSourceFilter.IsLyricsSearchEnabled(info.SourceApp, Settings.MediaSourceList))
            {
                await UpdateCurrentMediaUiAsync(() =>
                    ClearLyricsCandidates("当前为 SPlayer-Next：优先使用其外部 API 歌词；通用歌词搜索默认关闭。"));
                return;
            }

            if (!Settings.Lyrics.Sources.Any(source => source.IsEnabled))
            {
                await UpdateCurrentMediaUiAsync(() => ClearLyricsCandidates("没有启用的歌词来源。"));
                return;
            }

            var cancellation = new CancellationTokenSource();
            _lyricsCandidatesCancellation = cancellation;
            await UpdateCurrentMediaUiAsync(() => ClearLyricsCandidates("正在搜索已启用歌词源的候选..."));

            try
            {
                var candidates = await _lyricsSearchService.SearchCandidatesAsync(info, cancellation.Token);
                if (_isDetached || cancellation.IsCancellationRequested ||
                    searchVersion != Volatile.Read(ref _lyricsCandidatesSearchVersion))
                {
                    return;
                }

                await UpdateCurrentMediaUiAsync(() =>
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
                if (!_isDetached && searchVersion == Volatile.Read(ref _lyricsCandidatesSearchVersion))
                {
                    await UpdateCurrentMediaUiAsync(() => ClearLyricsCandidates("搜索歌词候选时发生错误。"));
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
                if (_isDetached || cancellation.IsCancellationRequested)
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
                if (!_isDetached)
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
            if (_isDetached)
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
            if (_isDetached)
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
            if (_isDetached)
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
            if (_isDetached)
            {
                return;
            }

            // 固定歌词不受清空缓存影响，说明这点免得用户以为固定也被清了。
            CurrentLyricsPinStatus = "已清空歌词缓存（固定的歌词保留）。";
        }

        private async Task RefreshLyricsPinStatusAsync(MediaInfo? mediaInfo)
        {
            if (mediaInfo is not { } info || string.IsNullOrWhiteSpace(info.Title))
            {
                CurrentLyricsPinStatus = "播放媒体后将在此处显示固定状态。";
                return;
            }

            var pin = await _lyricsSearchService.GetPinAsync(info);
            if (_isDetached)
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

        static bool IsLyricsIslandInstalled()
        {
            return IntegrationHelper.IsPluginInstalled("jiangyin14.lyrics");
        }

        static bool IsExtraIslandInstalled()
        {
            return IntegrationHelper.IsPluginInstalled("ink.lipoly.ext.extraisland");
        }
        public static bool IsLyricsIslandExisted()
        {
            return IsLyricsIslandInstalled() || IsExtraIslandInstalled();
        }

        private void AddButtonOnClick(object sender,RoutedEventArgs e)
        {
            if (_mediaService.CurrentMediaInfo == null)
            {
                CommonTaskDialogs.ShowDialog("添加媒体源时发生错误", "未检测到正在播放的媒体，请播放媒体后再试。");
                return;
            }

            var currentSource = _mediaService.CurrentMediaInfo.SourceApp;
            if (AddMediaSource(currentSource))
            {
                return;
            }

            CommonTaskDialogs.ShowDialog("添加媒体源时发生错误", "列表已存在该媒体源。");
        }

        private void AddCurrentMediaSourceIfAvailable()
        {
            if (_mediaService.CurrentMediaInfo == null)
            {
                return;
            }

            AddMediaSource(_mediaService.CurrentMediaInfo.SourceApp);
        }

        private bool AddMediaSource(string currentSource)
        {
            var sourceItem = new MediaSource
            {
                Source = currentSource
            };
            if (Settings.MediaSourceList.All(source => source?.Source != currentSource))
            {
                Settings.MediaSourceList.Add(sourceItem);
                _ = RefreshMediaSourceDisplayInfoAsync(sourceItem);
                SaveMediaSourceSettings();
                return true;
            }

            return false;
        }

        private void RemoveNullMediaSources()
        {
            var removed = false;
            for (var index = Settings.MediaSourceList.Count - 1; index >= 0; index--)
            {
                if (Settings.MediaSourceList[index] != null)
                {
                    continue;
                }

                Settings.MediaSourceList.RemoveAt(index);
                removed = true;
            }

            if (removed)
            {
                SaveMediaSourceSettings();
            }
        }

        private void DeleteButtonOnClick(object sender,RoutedEventArgs e)
        {
            Button button = (sender as Button)!;
            if (button.DataContext is MediaSource item)
            {
                Settings.MediaSourceList.Remove(item);
                _mediaSourceDisplayService.Invalidate(item.Source);
                SaveMediaSourceSettings();
            }
        }

        private async void ChooseIconButtonOnClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: MediaSource item })
            {
                return;
            }

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null)
            {
                return;
            }

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择播放源图标",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("图片")
                    {
                        Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp"]
                    }
                ]
            });

            var iconPath = files.FirstOrDefault()?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(iconPath))
            {
                return;
            }

            item.IconPath = iconPath;
            _mediaSourceDisplayService.Invalidate(item.Source);
            await RefreshMediaSourceDisplayInfoAsync(item);
            SaveMediaSourceSettings();
        }

        private async void ClearIconButtonOnClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: MediaSource item })
            {
                return;
            }

            item.IconPath = null;
            _mediaSourceDisplayService.Invalidate(item.Source);
            await RefreshMediaSourceDisplayInfoAsync(item);
            SaveMediaSourceSettings();
        }

        private async void CustomDisplayNameTextBoxOnLostFocus(object? sender, RoutedEventArgs e)
        {
            if (sender is not TextBox { DataContext: MediaSource item })
            {
                return;
            }

            _mediaSourceDisplayService.Invalidate(item.Source);
            await RefreshMediaSourceDisplayInfoAsync(item);
            await RefreshCurrentMediaInfoAsync(_mediaService.CurrentMediaInfo);
        }

        private async void RefreshMediaSourceDisplayInfos()
        {
            // 每个播放源的解析都可能触发一次进程扫描，串行 await 会让首次打开设置页
            // 的等待时间随播放源数量线性增长，这里并发解析。
            var sources = Settings.MediaSourceList.Where(source => source != null).ToArray();
            await Task.WhenAll(sources.Select(RefreshMediaSourceDisplayInfoAsync));
        }

        private async Task RefreshMediaSourceDisplayInfoAsync(MediaSource item)
        {
            if (string.IsNullOrWhiteSpace(item.Source))
            {
                item.DisplayName = string.Empty;
                item.DisplayIcon = null;
                item.IconStatus = "未设置";
                return;
            }

            item.IconStatus = "解析中";
            try
            {
                var displayInfo = await _mediaSourceDisplayService.ResolveAsync(item.Source);
                item.DisplayName = displayInfo.DisplayName;
                item.DisplayIcon = displayInfo.Icon;
                item.IconStatus = GetIconStatus(displayInfo.Kind, displayInfo.Icon != null);
            }
            catch
            {
                item.DisplayName = item.Source;
                item.DisplayIcon = null;
                item.IconStatus = "不可用";
            }
        }

        private static string GetIconStatus(MediaSourceDisplayKind kind, bool hasIcon)
        {
            return kind switch
            {
                MediaSourceDisplayKind.UserConfigured when hasIcon => "自定义",
                MediaSourceDisplayKind.Platform when hasIcon => "系统",
                MediaSourceDisplayKind.Bundled when hasIcon => "内置",
                MediaSourceDisplayKind.Mapping => "名称映射",
                MediaSourceDisplayKind.Unknown => "无图标",
                _ => hasIcon ? "已解析" : "无图标"
            };
        }

        private async Task RefreshCurrentMediaInfoAsync(MediaInfo? info)
        {
            if (info == null)
            {
                await UpdateCurrentMediaUiAsync(ClearCurrentMediaInfo);
                return;
            }

            try
            {
                var sourceInfo = await _mediaSourceDisplayService.ResolveAsync(info.SourceApp);
                await UpdateCurrentMediaUiAsync(() =>
                {
                    CurrentMediaTitle = string.IsNullOrWhiteSpace(info.Title) ? "未知标题" : info.Title;
                    CurrentMediaArtistAlbum = FormatArtistAlbum(info.Artist, info.AlbumTitle);
                    CurrentMediaPlaybackStatus = GetPlaybackStatusText(info.PlaybackInfo.PlaybackState);
                    CurrentMediaStatusGlyph = GetPlaybackStatusGlyph(info.PlaybackInfo.PlaybackState);
                    CurrentMediaTimeline = FormatTimeline(info.Position, info.Duration);
                    CurrentMediaSourceDisplay = $"播放源：{sourceInfo.DisplayName}";
                    CurrentMediaSourceId = $"播放源 ID：{info.SourceApp}";
                    CurrentMediaSourceIcon = sourceInfo.Icon;
                    CurrentMediaSourceIconStatus = $"图标状态：{GetIconStatus(sourceInfo.Kind, sourceInfo.Icon != null)}";
                    CurrentMediaThumbnail = info.Thumbnail;
                });
            }
            catch
            {
                await UpdateCurrentMediaUiAsync(() =>
                {
                    CurrentMediaTitle = string.IsNullOrWhiteSpace(info.Title) ? "未知标题" : info.Title;
                    CurrentMediaArtistAlbum = FormatArtistAlbum(info.Artist, info.AlbumTitle);
                    CurrentMediaPlaybackStatus = GetPlaybackStatusText(info.PlaybackInfo.PlaybackState);
                    CurrentMediaStatusGlyph = GetPlaybackStatusGlyph(info.PlaybackInfo.PlaybackState);
                    CurrentMediaTimeline = FormatTimeline(info.Position, info.Duration);
                    CurrentMediaSourceDisplay = $"播放源：{info.SourceApp}";
                    CurrentMediaSourceId = $"播放源 ID：{info.SourceApp}";
                    CurrentMediaSourceIcon = null;
                    CurrentMediaSourceIconStatus = "图标状态：不可用";
                    CurrentMediaThumbnail = info.Thumbnail;
                });
            }
        }

        private void ClearCurrentMediaInfo()
        {
            CurrentMediaTitle = "未检测到正在播放的媒体";
            CurrentMediaArtistAlbum = "播放媒体后会在此处显示标题、艺术家、专辑与进度。";
            CurrentMediaPlaybackStatus = "无媒体";
            CurrentMediaStatusGlyph = "\uE9CE";
            CurrentMediaTimeline = "00:00 / 00:00";
            CurrentMediaSourceDisplay = "播放源：-";
            CurrentMediaSourceId = "播放源 ID：-";
            CurrentMediaSourceIcon = null;
            CurrentMediaSourceIconStatus = "图标状态：无图标";
            CurrentMediaThumbnail = null;
        }

        private async Task UpdateCurrentMediaUiAsync(Action update)
        {
            if (_isDetached)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_isDetached)
                {
                    return;
                }

                update();
            });
        }

        private static string FormatArtistAlbum(string? artist, string? album)
        {
            var displayArtist = string.IsNullOrWhiteSpace(artist) ? "未知艺术家" : artist;
            var displayAlbum = string.IsNullOrWhiteSpace(album) ? "未知专辑" : album;
            return $"{displayArtist} - {displayAlbum}";
        }

        private static string FormatTimeline(TimeSpan position, TimeSpan duration)
        {
            return duration.Hours == 0
                ? $@"{position:mm\:ss} / {duration:mm\:ss}"
                : $@"{(int)position.TotalHours:00}:{position:mm\:ss} / {(int)duration.TotalHours:00}:{duration:mm\:ss}";
        }

        private static string GetPlaybackStatusText(MediaPlaybackState state)
        {
            return state switch
            {
                MediaPlaybackState.Playing => "正在播放",
                MediaPlaybackState.Paused => "已暂停",
                MediaPlaybackState.Stopped => "已停止",
                MediaPlaybackState.Changing => "切换中",
                MediaPlaybackState.Opened => "已打开",
                MediaPlaybackState.Closed => "已关闭",
                _ => "未知状态"
            };
        }

        private static string GetPlaybackStatusGlyph(MediaPlaybackState state)
        {
            return state switch
            {
                MediaPlaybackState.Playing => "\uEDB8",
                MediaPlaybackState.Paused => "\uEC90",
                MediaPlaybackState.Stopped => "\uF086",
                MediaPlaybackState.Changing => "\uE0B4",
                MediaPlaybackState.Opened => "\uEC2E",
                MediaPlaybackState.Closed => "\uE673",
                _ => "\uEE2F"
            };
        }

        private void SaveSettings()
        {
            ConfigureFileHelper.SaveConfig<PluginSettings>(Path.Combine(Plugin.globalConfigFolder!, "Settings.json"), Settings);
        }

        private void SaveButtonOnClick(object sender, RoutedEventArgs e)
        {
            SaveMediaSourceSettings();
        }

        private void SaveMediaSourceSettings()
        {
            SaveSettings();
            Settings.NotifyMediaSourceSettingsSaved();
        }

        private const string FollowGlobalFontOption = "跟随全局字体";

        /// <summary>
        /// 字体下拉选项：首项为“跟随全局字体”，其余为系统已安装字体。
        /// </summary>
        public IReadOnlyList<string> FontFamilyOptions { get; } = BuildFontFamilyOptions();

        public string LyricsOriginalFontFamilySelection
        {
            get => ToFontOption(Settings.LyricsOriginalFontFamily);
            set
            {
                Settings.LyricsOriginalFontFamily = FromFontOption(value);
                OnPropertyChanged();
                SaveSettings();
            }
        }

        public string LyricsTranslationFontFamilySelection
        {
            get => ToFontOption(Settings.LyricsTranslationFontFamily);
            set
            {
                Settings.LyricsTranslationFontFamily = FromFontOption(value);
                OnPropertyChanged();
                SaveSettings();
            }
        }

        public string LyricsRomanizationFontFamilySelection
        {
            get => ToFontOption(Settings.LyricsRomanizationFontFamily);
            set
            {
                Settings.LyricsRomanizationFontFamily = FromFontOption(value);
                OnPropertyChanged();
                SaveSettings();
            }
        }

        private static string ToFontOption(string? family) =>
            string.IsNullOrWhiteSpace(family) ? FollowGlobalFontOption : family;

        private static string FromFontOption(string? option) =>
            string.IsNullOrWhiteSpace(option) || option == FollowGlobalFontOption
                ? string.Empty
                : option;

        private static IReadOnlyList<string> BuildFontFamilyOptions()
        {
            var options = new List<string> { FollowGlobalFontOption };
            try
            {
                options.AddRange(FontManager.Current.SystemFonts
                    .Select(font => font.Name)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.CurrentCulture));
            }
            catch (Exception)
            {
                // 系统字体枚举失败时至少保留“跟随全局字体”，不影响设置页打开。
            }

            return options;
        }


        private void RefreshMediaLinkStatus()
        {
            if (_mediaLinkGateway is null)
            {
                MediaLinkStatusText = "共享服务不可用";
                MediaLinkExposureWarning = string.Empty;
                return;
            }

            if (!_mediaLinkGateway.IsRunning)
            {
                var stoppedReason = string.IsNullOrWhiteSpace(_mediaLinkGateway.LastError)
                    ? "未开启"
                    : $"无法启动：{_mediaLinkGateway.LastError}";
                MediaLinkStatusText = stoppedReason;
                MediaLinkExposureWarning = BuildExposureWarning();
                return;
            }

            var count = _mediaLinkGateway.ActiveSessionCount;
            var clients = count == 0 ? "暂无程序连接" : $"{count} 个程序已连接";
            var inject = _injectionStore is not null &&
                         (_injectionStore.HasExternalMedia || _injectionStore.HasExternalLyrics)
                ? "；正在显示外部来源的内容"
                : string.Empty;

            MediaLinkStatusText = $"运行中 · {_mediaLinkGateway.Endpoint ?? "-"} · {clients}{inject}";
            MediaLinkExposureWarning = BuildExposureWarning();
        }

        /// <summary>
        /// 监听地址非回环时给出明文暴露提示。传输无 TLS，同网段可嗅探 Token
        /// 与全部播放信息，这个代价必须让用户看见。
        /// </summary>
        private string BuildExposureWarning()
        {
            if (!Settings.MediaLinkIsEnabled)
            {
                return string.Empty;
            }

            var address = Settings.MediaLinkListenAddress?.Trim();
            if (string.IsNullOrEmpty(address))
            {
                return string.Empty;
            }

            var isLoopback = address is "127.0.0.1" or "::1" or "[::1]" ||
                             address.StartsWith("127.", StringComparison.Ordinal) ||
                             address.Equals("localhost", StringComparison.OrdinalIgnoreCase);

            return isLoopback
                ? string.Empty
                : "⚠ 当前设置允许局域网内的其它设备连接。传输过程未加密，同一网络下的人可能截获连接密钥与播放信息，请仅在自己信任的网络中这样设置。";
        }

        /// <summary>
        /// 订阅 gateway 的变更通知。此前用 1s DispatcherTimer 轮询：
        /// 界面最多滞后 1 秒，且页面停留期间持续空转。gateway 已实现
        /// INotifyPropertyChanged，事件驱动既即时又无空转。
        /// </summary>
        private void SubscribeMediaLinkGateway()
        {
            if (_mediaLinkGateway is null)
            {
                return;
            }

            _mediaLinkGateway.PropertyChanged += OnMediaLinkGatewayChanged;
        }

        private void SubscribeUpstream()
        {
            if (_upstream is not null)
            {
                _upstream.StateChanged += OnUpstreamStateChanged;
            }
        }

        private void UnsubscribeUpstream()
        {
            if (_upstream is not null)
            {
                _upstream.StateChanged -= OnUpstreamStateChanged;
            }
        }

        private void UnsubscribeMediaLinkGateway()
        {
            if (_mediaLinkGateway is null)
            {
                return;
            }

            _mediaLinkGateway.PropertyChanged -= OnMediaLinkGatewayChanged;
        }

        private void OnMediaLinkGatewayChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_isDetached)
            {
                return;
            }

            // gateway 的通知来自后台线程（listener 重建、会话增减），必须回到 UI 线程。
            Dispatcher.UIThread.Post(() =>
            {
                if (!_isDetached)
                {
                    RefreshMediaLinkStatus();
                }
            });
        }

        private void OnUpstreamStateChanged(object? sender, EventArgs e)
        {
            if (_isDetached)
            {
                return;
            }

            // 同样来自后台线程：重连循环与设置热更新都不在 UI 线程上。
            Dispatcher.UIThread.Post(() =>
            {
                if (!_isDetached)
                {
                    RefreshUpstreamStatus();
                }
            });
        }

        private void RefreshUpstreamStatus()
        {
            if (_upstream is null)
            {
                MediaLinkUpstreamStatusText = "接收服务不可用";
                return;
            }

            if (!Settings.MediaLinkUpstreamIsEnabled)
            {
                MediaLinkUpstreamStatusText = "未开启";
                return;
            }

            if (!string.IsNullOrWhiteSpace(_upstream.LastError))
            {
                MediaLinkUpstreamStatusText = $"未连接：{_upstream.LastError}";
                return;
            }

            // 来源设为「本机播放器」时收到的内容不会显示出来，这是最容易踩的坑。
            var modeHint = Settings.MediaLinkMediaSourceMode == MediaLinkMediaSourceMode.PlatformOnly
                ? "。⚠ 但「播放信息来源」当前为「本机播放器」，接收到的内容不会显示，请改为「外部来源优先」"
                : string.Empty;

            MediaLinkUpstreamStatusText = _upstream.IsConnected
                ? $"已连接{modeHint}"
                : $"正在连接对方设备…{modeHint}";
        }

        private void OnPluginSettingsChangedForMediaLink(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PluginSettings.MediaLinkMediaSourceMode))
            {
                OnPropertyChanged(nameof(MediaLinkMediaSourceModeIndex));
                RefreshMediaLinkStatus();
                RefreshUpstreamStatus();   // 提示语依赖当前媒体源模式
                return;
            }

            if (e.PropertyName is nameof(PluginSettings.MediaLinkUpstreamIsEnabled)
                or nameof(PluginSettings.MediaLinkUpstreamEndpoint)
                or nameof(PluginSettings.MediaLinkUpstreamToken))
            {
                RefreshUpstreamStatus();
                return;
            }

            if (e.PropertyName is nameof(PluginSettings.MediaLinkIsEnabled)
                or nameof(PluginSettings.MediaLinkListenAddress)
                or nameof(PluginSettings.MediaLinkPort)
                or nameof(PluginSettings.MediaLinkToken)
                or nameof(PluginSettings.MediaLinkTimelineMinIntervalMs)
                or nameof(PluginSettings.MediaLinkUiUsesEffective)
                or nameof(PluginSettings.MediaLinkPushUsesEffective))
            {
                // 暴露提示只取决于设置本身，立即更新；不能等 gateway 那 300ms，
                // 否则用户改成非回环地址后要过一会儿才看到警告。
                MediaLinkExposureWarning = BuildExposureWarning();

                // 运行状态是热更新异步完成的，短延迟后再读 gateway。
                Dispatcher.UIThread.Post(async () =>
                {
                    await Task.Delay(300);
                    if (!_isDetached)
                    {
                        RefreshMediaLinkStatus();
                    }
                });
            }
        }

        private async void CopyMediaLinkTokenOnClick(object? sender, RoutedEventArgs e)
        {
            var top = TopLevel.GetTopLevel(this);
            if (top?.Clipboard is null)
            {
                return;
            }

            await top.Clipboard.SetTextAsync(Settings.MediaLinkToken ?? string.Empty);
        }

        private void RegenerateMediaLinkTokenOnClick(object? sender, RoutedEventArgs e)
        {
            Settings.MediaLinkToken = MediaLinkAuth.GenerateToken();
            RefreshMediaLinkStatus();
        }

        /// <summary>
        /// 复制本机的配置码，供另一台设备一键导入，免去手抄 32 字节密钥。
        /// 配置码含密钥且未加密，故提示语必须说明这一点。
        /// </summary>
        private async void CopyMediaLinkConfigCodeOnClick(object? sender, RoutedEventArgs e)
        {
            var top = TopLevel.GetTopLevel(this);
            if (top?.Clipboard is null)
            {
                return;
            }

            var code = MediaLinkConfigCode.ForLocalInstance(
                Settings.MediaLinkListenAddress,
                Settings.MediaLinkPort,
                Settings.MediaLinkToken);

            if (code is null)
            {
                // 拿不到局域网地址时给出的配置码必然连不上，不如不给。
                MediaLinkConfigCodeHint = string.IsNullOrWhiteSpace(Settings.MediaLinkToken)
                    ? "请先开启共享以生成连接密钥"
                    : "无法获取本机的局域网地址，请手动把地址与密钥填到对方设备";
                return;
            }

            await top.Clipboard.SetTextAsync(code.Encode());
            MediaLinkConfigCodeHint = "已复制。这串文本包含连接密钥，请仅发给信任的设备。";
        }

        /// <summary>从剪贴板粘贴对方的配置码，一次填好地址与密钥。</summary>
        private async void PasteMediaLinkConfigCodeOnClick(object? sender, RoutedEventArgs e)
        {
            var top = TopLevel.GetTopLevel(this);
            if (top?.Clipboard is null)
            {
                return;
            }

            var text = await top.Clipboard.TryGetTextAsync();
            if (!MediaLinkConfigCode.TryParse(text, out var code, out var error))
            {
                MediaLinkUpstreamCodeHint = error ?? "配置码无法识别";
                return;
            }

            Settings.MediaLinkUpstreamEndpoint = code!.Endpoint;
            Settings.MediaLinkUpstreamToken = code.Token;
            MediaLinkUpstreamCodeHint = $"已填入 {code.Endpoint}";
            RefreshUpstreamStatus();
        }

        private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }


        private void LoadLyricsSettings()
        {
            _suppressLyricsSave = true;
            Settings.Lyrics = LyricsSourceSettings.Normalize(Settings.Lyrics);
            LyricsSourceItems.Clear();
            foreach (var source in Settings.Lyrics.Sources)
            {
                var item = new LyricsSourceItemViewModel(source, PersistLyricsSettings);
                LyricsSourceItems.Add(item);
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

            Settings.Lyrics = new LyricsSourceSettings
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
            };
            Settings.Lyrics = LyricsSourceSettings.Normalize(Settings.Lyrics);
            SaveSettings();
        }

        private void MoveLyricsSourceUpOnClick(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: LyricsSourceItemViewModel item })
            {
                return;
            }

            var index = LyricsSourceItems.IndexOf(item);
            if (index <= 0)
            {
                return;
            }

            LyricsSourceItems.Move(index, index - 1);
            PersistLyricsSettings();
        }

        private void MoveLyricsSourceDownOnClick(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: LyricsSourceItemViewModel item })
            {
                return;
            }

            var index = LyricsSourceItems.IndexOf(item);
            if (index < 0 || index >= LyricsSourceItems.Count - 1)
            {
                return;
            }

            LyricsSourceItems.Move(index, index + 1);
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

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            NotifyPropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public sealed class LyricsSourceItemViewModel : INotifyPropertyChanged
    {
        private bool _isEnabled;
        private bool _useWordSyncedLyrics;
        private int _globalOffsetMilliseconds;
        private string _globalOffsetMillisecondsText;
        private readonly Action _onChanged;

        public LyricsSourceItemViewModel(LyricsSourceEntry entry, Action onChanged)
        {
            Id = entry.Id;
            _isEnabled = entry.IsEnabled;
            _useWordSyncedLyrics = entry.UseWordSyncedLyrics;
            _globalOffsetMilliseconds = entry.GlobalOffsetMilliseconds;
            _globalOffsetMillisecondsText = _globalOffsetMilliseconds.ToString();
            _onChanged = onChanged;
        }

        public LyricsSourceId Id { get; }

        public string DisplayName => GetDisplayName(Id);

        public static string GetDisplayName(LyricsSourceId id) => id switch
        {
            LyricsSourceId.AmllTtml => "AMLL TTML DB",
            LyricsSourceId.QqMusic => "QQ 音乐",
            LyricsSourceId.Kugou => "酷狗音乐",
            LyricsSourceId.Netease => "网易云音乐",
            LyricsSourceId.SPlayerNext => "SPlayer-Next",
            LyricsSourceId.External => "外部来源",
            _ => id.ToString()
        };

        public string Capability => Id switch
        {
            LyricsSourceId.AmllTtml => "TTML 逐字",
            LyricsSourceId.QqMusic => "逐字 QRC / LRC",
            LyricsSourceId.Kugou => "逐字 KRC",
            LyricsSourceId.Netease => "LRC",
            LyricsSourceId.SPlayerNext => "外部 API 直出",
            _ => string.Empty
        };

        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled == value) return;
                _isEnabled = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
                _onChanged();
            }
        }

        public bool UseWordSyncedLyrics
        {
            get => _useWordSyncedLyrics;
            set
            {
                if (_useWordSyncedLyrics == value) return;
                _useWordSyncedLyrics = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UseWordSyncedLyrics)));
                _onChanged();
            }
        }

        public int GlobalOffsetMilliseconds
        {
            get => _globalOffsetMilliseconds;
            set
            {
                if (_globalOffsetMilliseconds == value) return;
                _globalOffsetMilliseconds = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GlobalOffsetMilliseconds)));
                _onChanged();
            }
        }

        public string GlobalOffsetMillisecondsText
        {
            get => _globalOffsetMillisecondsText;
            set
            {
                if (_globalOffsetMillisecondsText == value) return;
                _globalOffsetMillisecondsText = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GlobalOffsetMillisecondsText)));

                if (string.IsNullOrWhiteSpace(value) || !int.TryParse(value, out var offset))
                {
                    return;
                }

                GlobalOffsetMilliseconds = offset;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public sealed class LyricsCandidateItemViewModel
    {
        public LyricsCandidateItemViewModel(LyricsCandidate candidate)
        {
            Candidate = candidate;
            Source = LyricsSourceItemViewModel.GetDisplayName(candidate.Source);
            Title = string.IsNullOrWhiteSpace(candidate.Title) ? "未知标题" : candidate.Title;
            Artist = string.IsNullOrWhiteSpace(candidate.Artist) ? "未知艺术家" : candidate.Artist;
            Album = string.IsNullOrWhiteSpace(candidate.Album) ? "-" : candidate.Album;
            Score = candidate.Score;
            SyncCapability = candidate.SupportsWordSync ? "支持逐字" : "行级歌词";
        }

        public string Source { get; }

        public LyricsCandidate Candidate { get; }

        public string Title { get; }

        public string Artist { get; }

        public string Album { get; }

        public int Score { get; }

        public string SyncCapability { get; }
    }
}


