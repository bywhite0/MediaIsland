
using System.IO;
using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Extensions.Registry;
using ClassIsland.Shared.Helpers;
using MediaIsland.Components;
using MediaIsland.Models;
using MediaIsland.SettingsPages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MediaIsland
{
    [PluginEntrance]
    public class Plugin : PluginBase
    {
        public PluginSettings Settings { get; set; } = new();
        public static string? globalConfigFolder;
        public override void Initialize(HostBuilderContext context, IServiceCollection services)
        {
            Console.WriteLine("[MI]正在加载 MediaIsland...");
            services.AddComponent<NowPlayingComponent, NowPlayingComponentSettings>();
            services.AddComponent<SimplyNowPlayingComponent, SimplyNowPlayingComponentSettings>();
            globalConfigFolder = PluginConfigFolder; 
            Settings = ConfigureFileHelper.LoadConfig<PluginSettings>(Path.Combine(PluginConfigFolder, "Settings.json"));
            Settings.PropertyChanged += (sender, args) =>
            {
                ConfigureFileHelper.SaveConfig<PluginSettings>(Path.Combine(PluginConfigFolder, "Settings.json"), Settings);
            };
            services.AddSettingsPage<GeneralSettingsPage>();
#if !DEBUG
            if (Settings.IsTodayEatSentry)
            {
                SentrySdk.Init(o =>
                {
                    o.Dsn = "https://b86194d67d9ae75813f08deff24ce4f2@o4503936977666048.ingest.us.sentry.io/4509413290606592";
                    o.TracesSampleRate = 1.0;
                    o.Release = Info.Manifest.Version;
                    o.AutoSessionTracking = true;
                });
                // ClassIsland.Core.AppBase.Current.DispatcherUnhandledException += (_,e) => {
            //     if (e.Exception.StackTrace == null) SentrySdk.CaptureException(e.Exception);
            //     else if (e.Exception.StackTrace.Contains("MediaIsland")) SentrySdk.CaptureException(e.Exception);
            // };
            }
#endif
            Console.WriteLine("[MI]MediaIsland 加载成功");
        }

    }
}
