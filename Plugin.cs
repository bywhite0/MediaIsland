
using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Extensions.Registry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MediaIsland.Components;

namespace MediaIsland
{
    [PluginEntrance]
    public class Plugin : PluginBase
    {
        public override void Initialize(HostBuilderContext context, IServiceCollection services)
        {
            Console.WriteLine("[MI]正在加载 MediaIsland...");
            services.AddComponent<NowPlayingComponent>();
            Console.WriteLine("[MI]MediaIsland 加载成功");
        }

    }
}
