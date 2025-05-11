using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ClassIsland.Core.Abstractions.Services;

namespace MediaIsland.Helpers
{
    internal class IntegrationHelper
    {
        public static bool IsPluginInstalled(string pluginName)
        {
            return IPluginService.LoadedPlugins.Any(info => info.Manifest.Id == pluginName);
        }
    }
}
