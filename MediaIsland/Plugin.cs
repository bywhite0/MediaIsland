
using System.IO;
using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Extensions.Registry;
using ClassIsland.Shared.Helpers;
using MediaIsland.Components;
using MediaIsland.Models;
using MediaIsland.Services;
using MediaIsland.SettingsPages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MediaIsland
{
    [PluginEntrance]
    public class Plugin : PluginBase
    {
        public PluginSettings Settings { get; set; } = new();
        public override void Initialize(HostBuilderContext context, IServiceCollection services)
        {
            Console.WriteLine("[MI]正在加载 MediaIsland...");
            services.AddHostedService<MediaSessionService>();
            services.AddComponent<NowPlayingComponent, NowPlayingComponentSettings>();
            Settings = ConfigureFileHelper.LoadConfig<PluginSettings>(Path.Combine(PluginConfigFolder, "Settings.json"));
            Settings.PropertyChanged += (sender, args) =>
            {
                ConfigureFileHelper.SaveConfig<PluginSettings>(Path.Combine(PluginConfigFolder, "Settings.json"), Settings);
            };
            services.AddSettingsPage<IntegrationSettingsPage>();
            Console.WriteLine("[MI]MediaIsland 加载成功");
        }

    }
}
