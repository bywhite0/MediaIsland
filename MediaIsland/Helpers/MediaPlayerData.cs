// Modified from https://github.com/unchihugo/FluentFlyout/blob/v2.4.0/FluentFlyoutWPF/Classes/Utils/MediaPlayerData.cs
// Copyright (C) 2024 Hugo Li
// Licensed under the GPL-3。0 License.
using System.Diagnostics;
using Avalonia.Media.Imaging;

namespace MediaIsland.Helpers;

public static class MediaPlayerData
{
    private class CachedMediaPlayerInfo
    {
        public string Title { get; set; }
        public Bitmap? Icon { get; set; }
    }
    // cache for media player info to avoid redundant process lookups
    private static readonly Dictionary<string, CachedMediaPlayerInfo> MediaPlayerCache = new();

    private static Process[] cachedProcesses = null;
    private static DateTime lastCacheTime = DateTime.MinValue;
    private const int CacheDurationSeconds = 5;

    public static (string Title, Bitmap? Icon) GetMediaPlayerData(string mediaPlayerId)
    {
        if (MediaPlayerCache.TryGetValue(mediaPlayerId, out var cachedInfo))
        {
            return (cachedInfo.Title, cachedInfo.Icon);
        }

        string mediaTitle = mediaPlayerId;
        Bitmap? mediaIcon = null;
        
        // get sanitized media title name
        string[] mediaSessionIdVariants = mediaPlayerId.Split('.');

        // remove common non-informative substrings
        var variants = mediaSessionIdVariants.Select(variant =>
            variant.Replace("com", "", StringComparison.OrdinalIgnoreCase)
                   .Replace("github", "", StringComparison.OrdinalIgnoreCase)
                   .Replace("exe", "", StringComparison.OrdinalIgnoreCase)
                   .Trim()
        ).Where(variant => !string.IsNullOrWhiteSpace(variant)).ToList();

        // add original id to the end of the array to ensure at least one variant
        variants.Add(mediaPlayerId);
        
        var nameMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            //  ["Microsoft.ZuneMusic"] = "Groove 音乐",
            //  ["Microsoft.ZuneVideo"] = "电影与电视",
            //  ["msedge"] = "Edge",
            //  ["chrome"] = "Chrome",
            ["cloudmusic"] = "网易云音乐",
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


        Process[] processes;

        // use cache to avoid frequent process enumeration
        if (cachedProcesses == null || (DateTime.Now - lastCacheTime).TotalSeconds > CacheDurationSeconds)
        {
            cachedProcesses = Process.GetProcesses();
            lastCacheTime = DateTime.Now;
        }

        processes = cachedProcesses;

        var processData = processes.Select(p =>
            {
                try
                {
                    // pre-filter processes without a main window handle
                    if (p.MainWindowHandle == IntPtr.Zero)
                    {
                        return null;
                    }

                    var mainModule = p.MainModule;
                    if (mainModule == null) return null;

                    string path = mainModule.FileName;

                    if (variants.Any(v => path.Contains(v, StringComparison.OrdinalIgnoreCase)))
                    {
                        // prioritize the FileDescription for a user-friendly name
                        // fall back to MainWindowTitle if the description is empty
                        string title = !string.IsNullOrWhiteSpace(mainModule.FileVersionInfo.FileDescription)
                                        ? mainModule.FileVersionInfo.FileDescription
                                        : p.MainWindowTitle;

                        return new { Title = title, Path = path };
                    }
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // silently ignore the exception for inaccessible processes
                }
                return null;
            })
            .FirstOrDefault(data => data != null); // use first result

        if (processData != null)
        {
            mediaTitle = !string.IsNullOrWhiteSpace(processData.Title) ? processData.Title : mediaPlayerId;
            foreach (var key in nameMapping.Keys)
            {
                if (mediaPlayerId.Contains(key))
                {
                    mediaTitle = nameMapping[key];
                }
            }
            try
            {
                // using (var icon = System.Drawing.Icon.ExtractAssociatedIcon(processData.Path))
                // {
                //     if (icon != null)
                //     {
                //         mediaIcon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                //             icon.Handle,
                //             Int32Rect.Empty,
                //             BitmapSizeOptions.FromEmptyOptions());
                //
                //         mediaIcon.Freeze();
                //     }
                // }
            }
            catch
            {
                var internalIcon = GetInternalIcon(mediaPlayerId);
                if (internalIcon != null)
                {
                    mediaIcon = internalIcon;
                }
                else mediaIcon = null;
            }
        }
        else
        {
            foreach (var key in nameMapping.Keys)
            {
                if (mediaPlayerId.Contains(key))
                {
                    mediaTitle = nameMapping[key];
                }
            }
            var internalIcon = GetInternalIcon(mediaPlayerId);
            if (internalIcon != null)
            {
                mediaIcon = internalIcon;
            }
        }

        MediaPlayerCache[mediaPlayerId] = new CachedMediaPlayerInfo
        {
            Title = mediaTitle,
            Icon = mediaIcon
        };

        return (mediaTitle, mediaIcon);
    }
    private static Bitmap? GetInternalIcon(string appUserModelId)
    {
        try
        {
            return new Bitmap(($"avares://MediaIsland/Assets/SourceAppIcons/{appUserModelId}.png"));
        }
        catch
        {
            return null;
        }
    }
}