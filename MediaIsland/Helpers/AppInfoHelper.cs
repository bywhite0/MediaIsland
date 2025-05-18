using System.Diagnostics;
using Windows.ApplicationModel.Core;
using Windows.Management.Deployment;

namespace MediaIsland.Helpers
{
    public static class AppInfoHelper
    {

        public static async Task<string> GetFriendlyAppNameAsync(string userModelId)
        {
            if (string.IsNullOrWhiteSpace(userModelId)) return "未知播放器";

            // 增强名称映射表
            var nameMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                //  ["Microsoft.ZuneMusic"] = "Groove 音乐",
                //  ["Microsoft.ZuneVideo"] = "电影与电视",
                //  ["msedge"] = "Edge",
                //  ["chrome"] = "Chrome",
                //  ["cloudmusic"] = "网易云音乐",
                //  ["1F8B0F94.122165AE053F"] = "网易云音乐 UWP",
                //  ["QQMusic"] = "QQ 音乐",
                //  ["spotify"] = "Spotify",
                //  ["sim-music"] = "SimMusic",
                //  ["PotPlayer"] = "PotPlayer",
                ["lx-music"] = "LX Music",
                ["cn.toside.music.desktop"] = "LX Music",
                //  ["amll-player"] = "AMLL Player",
                ["net.stevexmh.amllplayer"] = "AMLL Player"
            };

            // 多级匹配策略
            foreach (var key in nameMapping.Keys)
            {
                if (userModelId.Contains(key))
                {
                    return nameMapping[key];
                }
            }
            // Win32 应用
            if (userModelId.Contains(".exe"))
            {
                var processName = userModelId[..^4];
                var processes = Process.GetProcessesByName(processName);
                Process? sessionProcess = null;
                
                if (processes?.Length > 0)
                {
                    sessionProcess = processes[0];
                }
                try
                {
                    return sessionProcess.MainModule.FileVersionInfo.FileDescription;
                }
                catch {  }
            }

            // UWP 应用
            try
            {
                var sourceApp = await GetAppListEntry(userModelId);
                return sourceApp.DisplayInfo.DisplayName;
            }
            catch { }


           



            // 未能匹配时显示技术性名称
            return ExtractCleanTechnicalName(userModelId);
        }
        static async Task<AppListEntry> GetAppListEntry(string userModelId)
        {
            var pm = new PackageManager();
            var packages = pm.FindPackagesForUser(string.Empty);
            var path = string.Empty;
            int currentAppIndex = -1;
            foreach (var package in packages)
                    {
                        var result = await package.GetAppListEntriesAsync();
                        for (int i = 0; i < result.Count; i++)
                        {
                            var app = result[i];

                            if (app.AppUserModelId == userModelId)
                            {
                                path = package.InstalledLocation.Path;
                                currentAppIndex = i;
                                return app;
                            }
                        }
                    }

            return null;
        }
        private static string ExtractCleanTechnicalName(string rawName)
        {
            if (rawName.Contains("_")) // 典型UWP标识特征
            {
                string[] parts = rawName.Split('!', '_');
                string appId = parts[0];

                // 示例1: "Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic"
                // 示例2: "NeteaseMusicUWP_1.0.0.0_x64__abcdefg"

                // 策略1：优先取应用ID（最后一个!后的内容）
                if (rawName.Contains("!"))
                {
                    return CleanTechnicalName(appId);
                }

                // 策略2：处理包名称中的品牌标识
                string packageName = parts.FirstOrDefault(p =>
                    !p.Any(char.IsDigit) && p.Length > 5 // 过滤版本号等数字部分
                );

                return CleanTechnicalName(packageName ?? rawName);
            }

            // 传统Win32应用处理
            return CleanTechnicalName(rawName);
        }

        private static string CleanTechnicalName(string technicalName)
        {
            return technicalName
                .Replace(".exe", "")
                .Replace("_", " ")
                .Trim()
                .Split('.', '-')
                .LastOrDefault()
                ?? "未知播放器";
        }
    }
}
