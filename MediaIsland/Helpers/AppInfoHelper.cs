using System;
using System.Collections.Generic;

namespace MediaIsland.Helpers
{
    public static class AppInfoHelper
    {
        public static string GetFriendlyAppName(string userModelId)
        {
            if (string.IsNullOrWhiteSpace(userModelId)) return "未知播放器";

            // 增强名称映射表
            var nameMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Microsoft.ZuneMusic"] = "Groove 音乐",
                ["Microsoft.ZuneVideo"] = "电影与电视",
                ["msedge"] = "Edge",
                ["chrome"] = "Chrome",
                ["cloudmusic"] = "网易云音乐",
                ["1F8B0F94.122165AE053F"] = "网易云音乐 UWP",
                ["QQMusic"] = "QQ 音乐",
                ["spotify"] = "Spotify",
                ["sim-music"] = "SimMusic",
                ["PotPlayer"] = "PotPlayer",
                ["lx-music"] = "LX Music",
                ["amll-player"] = "AMLL Player"
            };

            // 多级匹配策略
            foreach (var key in nameMapping.Keys)
            {
                if (userModelId.Contains(key))
                {
                    return nameMapping[key];
                }
            }

            // 未能匹配时显示技术性名称
            return ExtractCleanTechnicalName(userModelId);
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
