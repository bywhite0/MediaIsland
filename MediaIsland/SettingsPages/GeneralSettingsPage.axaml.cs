using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Controls;
using ClassIsland.Core.Enums.SettingsWindow;
using ClassIsland.Shared.Helpers;
using MediaIsland.Helpers;
using MediaIsland.Models;
using MediaIsland.Services.Media;

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
    public partial class GeneralSettingsPage : SettingsPageBase
    {
        public Plugin Plugin { get; }
        public PluginSettings Settings { get; }
        private readonly IMediaService _mediaService;

        public GeneralSettingsPage(Plugin plugin, IMediaService mediaService)
        {
            Plugin = plugin;
            Settings = Plugin.Settings;
            _mediaService = mediaService;
            InitializeComponent();
            DetachedFromVisualTree += (_, _) =>
            {
                _mediaService.MediaInfoChanged -= MediaService_OnMediaInfoChanged;
            };
            Settings.needRestart += RequestRestart;
            _mediaService.MediaInfoChanged += MediaService_OnMediaInfoChanged;
            StartMediaServiceAsync();
            AddCurrentMediaSourceIfAvailable();
            var screenshotApp = new MediaSource
            {
                Source = "Microsoft.ScreenSketch_8wekyb3d8bbwe!App",
                IsEnabled = false
            };
            if (!Settings.MediaSourceList.Any(source => source.Source == "Microsoft.ScreenSketch_8wekyb3d8bbwe!App"))
            {
                Settings.MediaSourceList.Add(screenshotApp);
                SaveSettings();
            }
        }

        private async void StartMediaServiceAsync()
        {
            try
            {
                await _mediaService.EnsureStartedAsync();
                AddCurrentMediaSourceIfAvailable();
            }
            catch
            {
                // Media source discovery is best-effort on the settings page.
            }
        }

        private void MediaService_OnMediaInfoChanged(object? sender, MediaInfoChangedEventArgs e)
        {
            if (e.MediaInfo == null)
            {
                return;
            }

            Dispatcher.UIThread.Post(() => AddMediaSource(e.MediaInfo.SourceApp));
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
            if (Settings.MediaSourceList.All(source => source.Source != currentSource))
            {
                Settings.MediaSourceList.Add(sourceItem);
                SaveSettings();
                return true;
            }

            return false;
        }

        private void DeleteButtonOnClick(object sender,RoutedEventArgs e)
        {
            Button button = (sender as Button)!;
            if (button.DataContext is MediaSource item)
            {
                Settings.MediaSourceList.Remove(item);
                SaveSettings();
            }
        }
        
        private void SaveSettings()
        {
            ConfigureFileHelper.SaveConfig<PluginSettings>(Path.Combine(Plugin.globalConfigFolder!, "Settings.json"), Settings);
        }

        private void SaveButtonOnClick(object sender, RoutedEventArgs e)
        {
            SaveSettings();
        }
    }
}
