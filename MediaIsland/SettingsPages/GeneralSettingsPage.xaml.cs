using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Windows.Media.Control;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Controls.CommonDialog;
using ClassIsland.Core.Enums.SettingsWindow;
using ClassIsland.Shared.Helpers;
using MaterialDesignThemes.Wpf;
using MediaIsland.Helpers;
using MediaIsland.Models;

namespace MediaIsland.SettingsPages
{
    /// <summary>
    /// GeneralSettingsPage.xaml 的交互逻辑
    /// </summary>
    [SettingsPageInfo(
        "mediaisland.general",
        "MediaIsland",
        PackIconKind.MusicBoxOutline,
        PackIconKind.MusicBox,
        SettingsPageCategory.External)]
    public partial class GeneralSettingsPage
    {
        public Plugin Plugin { get; }
        public PluginSettings Settings { get; }
        private GlobalSystemMediaTransportControlsSessionManager sessionManager;
        public GeneralSettingsPage(Plugin plugin)
        {
            Plugin = plugin;
            Settings = Plugin.Settings;
            InitializeComponent();
            DataContext = this;
            sessionManager = GlobalSystemMediaTransportControlsSessionManager.RequestAsync().GetAwaiter().GetResult();
            sessionManager.SessionsChanged += OnSessionsChanged;
            var screenshotApp = new MediaSource
            {
                Source = "Microsoft.ScreenSketch_8wekyb3d8bbwe!App",
                IsEnabled = false
            };
            if (Settings.MediaSourceList.Any(source => source.Source == "Microsoft.ScreenSketch_8wekyb3d8bbwe!App")) return;
            Settings.MediaSourceList.Add(screenshotApp);
            SaveSettings();
            // TODO: Remove after ExtraIsland's new version release
            if (Settings.IsLXMusicLyricForwarderEnabled)
            {
                if (IsLyricsIslandInstalled() || IsExtraIslandInstalled())
                {
                    LXMusicsLyricForwarderSwitcher.IsEnabled = true;
                }
            }
        }

        private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
        {
            if (sessionManager.GetCurrentSession() == null)
            {
                return;
            }
            var currentSource = sessionManager.GetCurrentSession().SourceAppUserModelId;
            var sourceItem = new MediaSource
            {
                Source = currentSource
            };
            if (Settings.MediaSourceList.Any(source => source.Source == currentSource)) return;
            Settings.MediaSourceList.Add(sourceItem);
            SaveSettings();
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
        // TODO: Remove after ExtraIsland's new version release
        void LXMLFSwitcher_Click(object sender, RoutedEventArgs e)
        {

        }
        private void StartLXMusicLyricsForwarder_OnClick(object sender, RoutedEventArgs e)
        {
            var LyricsForwarder = new LXMusicLyricsHelper(Settings);
            Task.Run(async () => await LyricsForwarder.LyricsForwarderAsync());
        }

        private void AddButtonOnClick(object sender,RoutedEventArgs e)
        {
            if (sessionManager.GetCurrentSession() == null)
            {
                CommonDialog.ShowError("未检测到正在播放的媒体，请播放媒体后再试。");
                return;
            }
            var currentSource = sessionManager.GetCurrentSession().SourceAppUserModelId;
            var sourceItem = new MediaSource
            {
                Source = currentSource
            };
            if (Settings.MediaSourceList.All(source => source.Source != currentSource))
            {
                Settings.MediaSourceList.Add(sourceItem);
                SaveSettings();
            }   
            else
            {
                CommonDialog.ShowError("列表已存在该媒体源，");
            }
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
