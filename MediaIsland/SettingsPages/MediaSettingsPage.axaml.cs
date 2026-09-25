using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Controls;
using MediaIsland.Models;
using MediaIsland.Services.Media;
using MediaIsland.Services.Media.SourceDisplay;
using MediaIsland.Services.MediaLink;

namespace MediaIsland.SettingsPages;

/// <summary>
/// 「媒体」页：当前媒体、播放源列表与隐私。沿用旧总页的 id，ClassIsland 记住的上次页面不失效。
/// </summary>
[SettingsPageInfo("mediaisland.general", "媒体", "\uEDBB", "\uEDBA")]
[Group(GroupId)]
public partial class MediaSettingsPage : MediaIslandSettingsPage
{
    private const string ScreenSketchSource = "Microsoft.ScreenSketch_8wekyb3d8bbwe!App";

    private readonly IMediaService _mediaService;
    private readonly IMediaSourceDisplayService _mediaSourceDisplayService;
    private readonly IEffectiveMediaSource? _effectiveMediaSource;

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
            if (SetProperty(ref _currentMediaThumbnail, value))
            {
                OnPropertyChanged(nameof(HasCurrentMediaThumbnail));
            }
        }
    }

    public bool HasCurrentMediaThumbnail => CurrentMediaThumbnail != null;

    public Bitmap? CurrentMediaSourceIcon
    {
        get => _currentMediaSourceIcon;
        private set
        {
            if (SetProperty(ref _currentMediaSourceIcon, value))
            {
                OnPropertyChanged(nameof(HasCurrentMediaSourceIcon));
            }
        }
    }

    public bool HasCurrentMediaSourceIcon => CurrentMediaSourceIcon != null;

    public MediaSettingsPage(
        Plugin plugin,
        IMediaService mediaService,
        IMediaSourceDisplayService mediaSourceDisplayService,
        IEffectiveMediaSource? effectiveMediaSource = null) : base(plugin)
    {
        _mediaService = mediaService;
        _mediaSourceDisplayService = mediaSourceDisplayService;
        _effectiveMediaSource = effectiveMediaSource;
        RemoveNullMediaSources();
        InitializeComponent();

        // 目前只有 Spotify 水印开关会请求重启，它就在本页。
        Settings.needRestart += RequestRestart;
        if (_effectiveMediaSource is not null)
        {
            _effectiveMediaSource.EffectiveMediaChanged += OnMediaInfoChanged;
        }
        else
        {
            _mediaService.MediaInfoChanged += OnMediaInfoChanged;
        }

        StartMediaServiceAsync();
        AddCurrentMediaSourceIfAvailable();
        _ = RefreshCurrentMediaInfoAsync(CurrentUiMediaInfo);
        RefreshMediaSourceDisplayInfos();
        if (Settings.MediaSourceList.All(source => source?.Source != ScreenSketchSource))
        {
            Settings.MediaSourceList.Add(new MediaSource { Source = ScreenSketchSource, IsEnabled = false });
            SaveMediaSourceSettings();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Settings.needRestart -= RequestRestart;
        if (_effectiveMediaSource is not null)
        {
            _effectiveMediaSource.EffectiveMediaChanged -= OnMediaInfoChanged;
        }

        _mediaService.MediaInfoChanged -= OnMediaInfoChanged;
    }

    private MediaInfo? CurrentUiMediaInfo =>
        _effectiveMediaSource.GetCurrentUiMediaInfo(_mediaService);

    private async void StartMediaServiceAsync()
    {
        try
        {
            await _mediaService.EnsureStartedAsync();
            AddCurrentMediaSourceIfAvailable();
            await RefreshCurrentMediaInfoAsync(CurrentUiMediaInfo);
        }
        catch
        {
            // Media source discovery is best-effort on the settings page.
        }
    }

    private void OnMediaInfoChanged(object? sender, MediaInfoChangedEventArgs e)
    {
        _ = RefreshCurrentMediaInfoAsync(e.MediaInfo);
        if (e.MediaInfo == null)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => AddMediaSource(e.MediaInfo.SourceApp));
    }

    private void AddButtonOnClick(object sender, RoutedEventArgs e)
    {
        if (_mediaService.CurrentMediaInfo == null)
        {
            CommonTaskDialogs.ShowDialog("添加媒体源时发生错误", "未检测到正在播放的媒体，请播放媒体后再试。");
            return;
        }

        if (AddMediaSource(_mediaService.CurrentMediaInfo.SourceApp))
        {
            return;
        }

        CommonTaskDialogs.ShowDialog("添加媒体源时发生错误", "列表已存在该媒体源。");
    }

    private void AddCurrentMediaSourceIfAvailable()
    {
        if (_mediaService.CurrentMediaInfo != null)
        {
            AddMediaSource(_mediaService.CurrentMediaInfo.SourceApp);
        }
    }

    private bool AddMediaSource(string currentSource)
    {
        if (Settings.MediaSourceList.Any(source => source?.Source == currentSource))
        {
            return false;
        }

        var sourceItem = new MediaSource { Source = currentSource };
        Settings.MediaSourceList.Add(sourceItem);
        _ = RefreshMediaSourceDisplayInfoAsync(sourceItem);
        SaveMediaSourceSettings();
        return true;
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

    private void DeleteButtonOnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: MediaSource item })
        {
            Settings.MediaSourceList.Remove(item);
            _mediaSourceDisplayService.Invalidate(item.Source);
            SaveMediaSourceSettings();
        }
    }

    private async void ChooseIconButtonOnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: MediaSource item } ||
            TopLevel.GetTopLevel(this) is not { } topLevel)
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
            await PostToUiAsync(ClearCurrentMediaInfo);
            return;
        }

        try
        {
            var sourceInfo = await _mediaSourceDisplayService.ResolveAsync(info.SourceApp);
            await PostToUiAsync(() =>
            {
                ApplyMediaInfo(info);
                CurrentMediaSourceDisplay = $"播放源：{sourceInfo.DisplayName}";
                CurrentMediaSourceIcon = sourceInfo.Icon;
                CurrentMediaSourceIconStatus = $"图标状态：{GetIconStatus(sourceInfo.Kind, sourceInfo.Icon != null)}";
            });
        }
        catch
        {
            await PostToUiAsync(() =>
            {
                ApplyMediaInfo(info);
                CurrentMediaSourceDisplay = $"播放源：{info.SourceApp}";
                CurrentMediaSourceIcon = null;
                CurrentMediaSourceIconStatus = "图标状态：不可用";
            });
        }
    }

    private void ApplyMediaInfo(MediaInfo info)
    {
        CurrentMediaTitle = string.IsNullOrWhiteSpace(info.Title) ? "未知标题" : info.Title;
        CurrentMediaArtistAlbum = FormatArtistAlbum(info.Artist, info.AlbumTitle);
        CurrentMediaPlaybackStatus = GetPlaybackStatusText(info.PlaybackInfo.PlaybackState);
        CurrentMediaStatusGlyph = GetPlaybackStatusGlyph(info.PlaybackInfo.PlaybackState);
        CurrentMediaTimeline = FormatTimeline(info.Position, info.Duration);
        CurrentMediaSourceId = $"播放源 ID：{info.SourceApp}";
        CurrentMediaThumbnail = info.Thumbnail;
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

    private void SaveButtonOnClick(object sender, RoutedEventArgs e)
    {
        SaveMediaSourceSettings();
    }

    private void SaveMediaSourceSettings()
    {
        SaveSettings();
        Settings.NotifyMediaSourceSettingsSaved();
    }
}
