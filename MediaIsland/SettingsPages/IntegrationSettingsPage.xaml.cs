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
    /// IntegrationSettingsPage.xaml 的交互逻辑
    /// </summary>
    [SettingsPageInfo(
        "mediaisland.integration",
        "MediaIsland 集成",
        PackIconKind.Select,
        PackIconKind.SelectAll,
        SettingsPageCategory.External)]
    public partial class IntegrationSettingsPage : SettingsPageBase
    {
        public Plugin Plugin { get; }
        public PluginSettings Settings { get; }
        public IntegrationSettingsPage(Plugin plugin)
        {
            Plugin = plugin;
            Settings = new PluginSettings();
            InitializeComponent();
            DataContext = this;
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
