
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Extensions.Registry;
using ClassIsland.Shared.Helpers;
using MediaIsland.Components;
using MediaIsland.Models;
using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Lyrics.Models;
using MediaIsland.Services.Lyrics.Native;
using MediaIsland.Services.Lyrics.Parsers;
using MediaIsland.Services.Lyrics.Providers;
using MediaIsland.Services.Media;
using MediaIsland.Services.Media.Platform;
using MediaIsland.Services.Media.SourceDisplay;
using MediaIsland.SettingsPages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MediaIsland
{
    [PluginEntrance]
    public class Plugin : PluginBase
    {
        public PluginSettings Settings { get; set; } = new();
        public static Plugin? Instance { get; private set; }
        public static string? globalConfigFolder;
        public override void Initialize(HostBuilderContext context, IServiceCollection services)
        {
            Instance = this;
            Console.WriteLine("[MI]正在加载 MediaIsland...");
            services.AddSingleton<NoOpMediaSourceInfoProvider>();
            RegisterPlatformProviders(services);
            services.AddSingleton<IMediaPlatformProvider, NoOpMediaPlatformProvider>();
            services.AddSingleton<MediaPlatformProviderResolver>();
            services.AddSingleton<MediaService>();
            services.AddSingleton<IMediaService>(provider => provider.GetRequiredService<MediaService>());
            services.AddSingleton<MediaSourceDisplayService>();
            services.AddSingleton<IMediaSourceDisplayService>(provider => provider.GetRequiredService<MediaSourceDisplayService>());
            services.AddSingleton<TtmlNativeParser>();
            services.AddSingleton<ILyricsProvider, AmllTtmlLyricsProvider>();
            services.AddSingleton<ILyricsProvider, QqMusicLyricsProvider>();
            services.AddSingleton<ILyricsProvider, KugouLyricsProvider>();
            services.AddSingleton<ILyricsProvider, NeteaseLyricsProvider>();
            services.AddSingleton<ILyricsPayloadParser, ManagedLyricsPayloadParser>();
            services.AddSingleton<ILyricsPayloadParser, TtmlLyricsPayloadParser>();
            services.AddSingleton<LyricsSearchService>(provider => new LyricsSearchService(
                provider.GetServices<ILyricsProvider>(),
                provider.GetServices<ILyricsPayloadParser>(),
                () => (Instance ?? throw new InvalidOperationException("MediaIsland 插件尚未初始化。")).Settings.Lyrics,
                provider.GetService<Microsoft.Extensions.Logging.ILogger<LyricsSearchService>>()));
            services.AddHostedService(provider => provider.GetRequiredService<MediaService>());
            services.AddComponent<NowPlayingComponent, NowPlayingComponentSettings>();
            services.AddComponent<SimplyNowPlayingComponent, SimplyNowPlayingComponentSettings>();
            services.AddComponent<LyricsComponent, LyricsComponentSettings>();
            globalConfigFolder = PluginConfigFolder; 
            Settings = ConfigureFileHelper.LoadConfig<PluginSettings>(Path.Combine(PluginConfigFolder, "Settings.json"));
            Settings.Lyrics = LyricsSourceSettings.Normalize(Settings.Lyrics);
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

        private static void RegisterPlatformProviders(IServiceCollection services)
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            try
            {
                var assembly = LoadOptionalProviderAssembly("MediaIsland.Windows");
                var registrationType = assembly.GetType(
                    "MediaIsland.Services.Media.Platform.Windows.WindowsMediaPlatformProviderRegistration",
                    throwOnError: true);
                var registrationMethod = registrationType?.GetMethod(
                    "AddWindowsMediaPlatformProvider",
                    BindingFlags.Public | BindingFlags.Static);

                if (registrationMethod == null)
                {
                    Console.WriteLine("[MI]Windows media provider registration method not found.");
                    return;
                }

                registrationMethod.Invoke(null, new object[] { services });
                Console.WriteLine("[MI]Windows media provider registered.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MI]Failed to register Windows media provider: {ex.Message}");
            }
        }

        private static Assembly LoadOptionalProviderAssembly(string assemblyName)
        {
            var pluginAssembly = typeof(Plugin).Assembly;
            var loadContext = AssemblyLoadContext.GetLoadContext(pluginAssembly);
            var pluginFolder = Path.GetDirectoryName(pluginAssembly.Location) ?? AppContext.BaseDirectory;
            var providerPath = Path.Combine(pluginFolder, $"{assemblyName}.dll");

            if (File.Exists(providerPath) && loadContext != null)
            {
                return loadContext.LoadFromAssemblyPath(providerPath);
            }

            return Assembly.Load(new AssemblyName(assemblyName));
        }

    }
}
