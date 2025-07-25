using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Enums.SettingsWindow;
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
        public GeneralSettingsPage(Plugin plugin)
        {
            Plugin = plugin;
            Settings = Plugin.Settings;
            InitializeComponent();
            DataContext = this;
            // TODO: Remove after ExtraIsland's new version release
            if (Settings.IsLXMusicLyricForwarderEnabled)
            {
                if (IsLyricsIslandInstalled() || IsExtraIslandInstalled())
                {
                    LXMusicsLyricForwarderSwitcher.IsEnabled = true;
                }
            }
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
    }
}
