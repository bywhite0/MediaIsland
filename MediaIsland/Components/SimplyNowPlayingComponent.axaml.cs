using System.IO;
using Avalonia.Controls;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Shared.Helpers;
using MediaIsland.Helpers;
using MediaIsland.Models;
using MediaIsland.Services.Media;
using Microsoft.Extensions.Logging;
using RoutedEventArgs = Avalonia.Interactivity.RoutedEventArgs;

namespace MediaIsland.Components
{
    [ComponentInfo(
            "038A3FB9-946A-48B6-871C-11FFDC2BA09C",
            "正在播放(简)",
            "\uEBC9",
            "简略显示当前播放的媒体。"
        )]
    // ReSharper disable once ClassNeverInstantiated.Global
    public partial class SimplyNowPlayingComponent : ComponentBase<SimplyNowPlayingComponentConfig>
    {
        private readonly IMediaService _mediaService;
        private ILogger<SimplyNowPlayingComponent> Logger { get; }

        private PluginSettings globalSettings;
        private bool _isLoaded;
        private MediaInfo? _currentMediaInfo;

        public SimplyNowPlayingComponent(ILogger<SimplyNowPlayingComponent> logger, IMediaService mediaService)
        {
            InitializeComponent();
            Logger = logger;
            _mediaService = mediaService;
            globalSettings = Plugin.Instance?.Settings ??
                             ConfigureFileHelper.LoadConfig<PluginSettings>(Path.Combine(Plugin.globalConfigFolder!, "Settings.json"));
        }

        private void SimplyNowPlayingComponent_OnLoaded(object sender, RoutedEventArgs e)
        {
            _isLoaded = true;
            globalSettings = Plugin.Instance?.Settings ?? globalSettings;
            globalSettings.MediaSourceSettingsSaved -= GlobalSettings_OnMediaSourceSettingsSaved;
            globalSettings.MediaSourceSettingsSaved += GlobalSettings_OnMediaSourceSettingsSaved;
            Settings.PropertyChanged += OnSettingsPropertyChanged;
            LoadCurrentPlayingInfoAsync();
        }

        private void SimplyNowPlayingComponent_OnUnloaded(object sender, RoutedEventArgs e)
        {
            _isLoaded = false;
            globalSettings.MediaSourceSettingsSaved -= GlobalSettings_OnMediaSourceSettingsSaved;
            Settings.PropertyChanged -= OnSettingsPropertyChanged;
            _mediaService.MediaInfoChanged -= MediaService_OnMediaInfoChanged;
        }

        private void GlobalSettings_OnMediaSourceSettingsSaved(object? sender, EventArgs e)
        {
            if (!_isLoaded)
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (_mediaService.CurrentMediaInfo is { } mediaInfo)
                {
                    _ = RefreshMediaInfo(mediaInfo);
                }
                else
                {
                    _ = HideMediaGridAsync();
                }
            });
        }

        private void OnSettingsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case "IsHideWhenPaused":
                    if (_currentMediaInfo?.PlaybackInfo.PlaybackState == MediaPlaybackState.Paused)
                    {
                        Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            MediaGrid.IsVisible = !Settings.IsHideWhenPaused;
                        });
                    }

                    break;
                case "InfoType":
                    Dispatcher.UIThread.InvokeAsync(ApplyInfoType);
                    break;
            }
        }

        /// <summary>
        /// 获取媒体服务信息并更新 UI
        /// </summary>
        // ReSharper disable once AsyncVoidMethod
        private async void LoadCurrentPlayingInfoAsync()
        {
            _mediaService.MediaInfoChanged -= MediaService_OnMediaInfoChanged;
            _mediaService.MediaInfoChanged += MediaService_OnMediaInfoChanged;

            try
            {
                await _mediaService.EnsureStartedAsync();
                Logger.LogInformation("尝试获取媒体会话信息");
                if (_mediaService.CurrentMediaInfo != null)
                {
                    Logger.LogInformation("存在媒体会话信息");
                    Logger.LogDebug("刷新【正在播放】组件内容");
                    await RefreshMediaInfo(_mediaService.CurrentMediaInfo);
                }
                else
                {
                    Logger.LogInformation("不存在媒体会话信息，隐藏组件 UI");
                    await HideMediaGridAsync();
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("获取媒体会话时发生错误: {ExMessage}", ex.Message);
                await HideMediaGridAsync();
            }
        }

        // ReSharper disable once AsyncVoidMethod
        private async void MediaService_OnMediaInfoChanged(object? sender, MediaInfoChangedEventArgs e)
        {
            if (!_isLoaded)
            {
                return;
            }

            _currentMediaInfo = e.MediaInfo;
            if (e.MediaInfo == null)
            {
                await HideMediaGridAsync();
                return;
            }

            if (!MediaSourceFilter.IsEnabled(e.MediaInfo.SourceApp, globalSettings.MediaSourceList))
            {
                Logger.LogInformation("当前媒体会话 [{SourceApp}] 已禁用，自动隐藏", e.MediaInfo.SourceApp);
                await HideMediaGridAsync();
                return;
            }

            switch (e.ChangeKind)
            {
                case MediaInfoChangeKind.Playback:
                    await RefreshPlaybackInfo(e.MediaInfo);
                    break;
                case MediaInfoChangeKind.CurrentSession:
                case MediaInfoChangeKind.MediaProperties:
                case MediaInfoChangeKind.Timeline:
                default:
                    await RefreshMediaInfo(e.MediaInfo);
                    break;
            }
        }

        /// <summary>
        /// 使用获取到的媒体信息刷新 UI
        /// </summary>
        private async Task RefreshMediaInfo(MediaInfo mediaInfo)
        {
            _currentMediaInfo = mediaInfo;
            if (!_isLoaded)
            {
                return;
            }

            try
            {
                if (!MediaSourceFilter.IsEnabled(mediaInfo.SourceApp, globalSettings.MediaSourceList))
                {
                    Logger.LogInformation("当前媒体会话 [{SourceApp}] 已禁用，自动隐藏", mediaInfo.SourceApp);
                    await HideMediaGridAsync();
                    return;
                }

                Logger.LogTrace(
                    "当前媒体信息：{Artist} - {Title} ({PlaybackStatus}) [{TimelinePosition} / {TimelineEndTime}]",
                    mediaInfo.Artist,
                    mediaInfo.Title,
                    mediaInfo.PlaybackInfo.PlaybackState,
                    mediaInfo.Position,
                    mediaInfo.Duration);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ApplyPlaybackInfo(mediaInfo);

                    TitleText.Text = mediaInfo.Title ?? "未知标题";
                    ArtistText.Text = mediaInfo.Artist ?? "未知艺术家";
                    DualTitleText.Text = mediaInfo.Title ?? "未知标题";
                    DualArtistText.Text = mediaInfo.Artist ?? "未知艺术家";

                    ApplyInfoType();
                });
            }
            catch (Exception ex)
            {
                Logger.LogError("获取媒体信息失败：{ExMessage}", ex.Message);
            }
        }

        private async Task RefreshPlaybackInfo(MediaInfo mediaInfo)
        {
            if (!_isLoaded)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ApplyPlaybackInfo(mediaInfo);
            });
        }

        private void ApplyPlaybackInfo(MediaInfo mediaInfo)
        {
            if (mediaInfo.PlaybackInfo.PlaybackState == MediaPlaybackState.Paused)
            {
                MediaGrid.IsVisible = !Settings.IsHideWhenPaused;
            }
            else
            {
                MediaGrid.IsVisible = true;
            }

            StatusIcon.Glyph = mediaInfo.PlaybackInfo.PlaybackState switch
            {
                MediaPlaybackState.Playing => "\uEDB8",
                MediaPlaybackState.Paused => "\uEC90",
                MediaPlaybackState.Stopped => "\uF086",
                MediaPlaybackState.Changing => "\uE0B4",
                _ => StatusIcon.Glyph
            };
        }

        private void ApplyInfoType()
        {
            switch (Settings.InfoType)
            {
                case 0:
                    DividerText.IsVisible = true;
                    ArtistText.IsVisible = true;
                    Grid.SetColumn(TitleText, 2);
                    Grid.SetColumn(ArtistText, 0);
                    break;
                case 1:
                    DividerText.IsVisible = true;
                    ArtistText.IsVisible = true;
                    Grid.SetColumn(TitleText, 0);
                    Grid.SetColumn(ArtistText, 2);
                    break;
                case 2:
                    DividerText.IsVisible = false;
                    ArtistText.IsVisible = false;
                    Grid.SetColumn(TitleText, 0);
                    Grid.SetColumn(ArtistText, 2);
                    break;
            }

            Settings.IsDualLineStyle = Settings.InfoType == 3;
        }

        private async Task HideMediaGridAsync()
        {
            if (!_isLoaded)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                MediaGrid.IsVisible = false;
            });
        }

    }
}
