using System.IO;
using Windows.Media.Control;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Controls;
using ClassIsland.Core.Enums.SettingsWindow;
using ClassIsland.Shared.Helpers;
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
        "\uEBCA",
        "\uEBC9",
        SettingsPageCategory.External)]
    public partial class GeneralSettingsPage : SettingsPageBase
    {
        public Plugin Plugin { get; }
        public PluginSettings Settings { get; }
        private GlobalSystemMediaTransportControlsSessionManager sessionManager;
        public GeneralSettingsPage(Plugin plugin)
        {
            Plugin = plugin;
            Settings = Plugin.Settings;
            InitializeComponent();
            sessionManager = GlobalSystemMediaTransportControlsSessionManager.RequestAsync().GetAwaiter().GetResult();
            sessionManager.SessionsChanged += OnSessionsChanged;
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
        
        private void AddButtonOnClick(object sender,RoutedEventArgs e)
        {
            if (sessionManager.GetCurrentSession() == null)
            {
                CommonTaskDialogs.ShowDialog("添加媒体源时发生错误", "未检测到正在播放的媒体，请播放媒体后再试。");
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
                CommonTaskDialogs.ShowDialog("添加媒体源时发生错误", "列表已存在该媒体源。");
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
