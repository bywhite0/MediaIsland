using System.Diagnostics;
using System.IO;
using Avalonia.Media.Imaging;
using MediaIsland.Helpers;
using MediaIsland.Windows.Helpers;
using Microsoft.Extensions.Logging;
using Windows.ApplicationModel;
using Windows.Management.Deployment;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace MediaIsland.Services.Media.Platform.Windows;

public sealed class WindowsMediaSourceInfoProvider(
    ILogger<WindowsMediaSourceInfoProvider> logger) : IMediaSourceInfoProvider
{
    public async Task<MediaSourceInfo?> ResolveAsync(
        string sourceApp,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceApp))
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (TryGetPackageFamilyName(sourceApp, out var packageFamilyName))
        {
            var packageInfo = await ResolvePackageAsync(sourceApp, packageFamilyName, cancellationToken);
            if (packageInfo != null)
            {
                return packageInfo;
            }
        }

        return await ResolveProcessAsync(sourceApp, cancellationToken);
    }

    private async Task<MediaSourceInfo?> ResolvePackageAsync(
        string sourceApp,
        string packageFamilyName,
        CancellationToken cancellationToken)
    {
        try
        {
            var package = new PackageManager()
                .FindPackagesForUser(string.Empty, packageFamilyName)
                .FirstOrDefault();
            if (package == null)
            {
                return null;
            }

            var displayName = ResolvePackageDisplayName(package, sourceApp);
            var icon = await LoadPackageLogoAsync(package, cancellationToken);
            return new MediaSourceInfo(sourceApp, displayName, icon, MediaSourceInfoKind.Platform);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "无法解析封装应用媒体源信息：{SourceApp}", sourceApp);
            return null;
        }
    }

    private async Task<MediaSourceInfo?> ResolveProcessAsync(
        string sourceApp,
        CancellationToken cancellationToken)
    {
        // 全进程扫描是同步阻塞调用（数百毫秒），必须挪出调用线程，否则会卡住 UI。
        var processPath = await Task.Run(() => TryFindProcessPath(sourceApp), cancellationToken);
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var displayName = ResolveExecutableDisplayName(processPath, sourceApp);
        var icon = await LoadFileThumbnailAsync(processPath, cancellationToken);
        return new MediaSourceInfo(sourceApp, displayName, icon, MediaSourceInfoKind.Platform);
    }

    private async Task<Bitmap?> LoadPackageLogoAsync(
        Package package,
        CancellationToken cancellationToken)
    {
        var logoPath = TryResolvePackageLogoPath(package);
        if (string.IsNullOrWhiteSpace(logoPath))
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return await LoadBitmapFileAsync(logoPath, cancellationToken);
    }

    private static string? TryResolvePackageLogoPath(Package package)
    {
        var installedPath = package.InstalledLocation.Path;
        var logoLocalPath = package.Logo.LocalPath.TrimStart('\\', '/');
        var exactPath = Path.Combine(installedPath, logoLocalPath);
        if (File.Exists(exactPath))
        {
            return exactPath;
        }

        var logoDirectory = Path.GetDirectoryName(exactPath);
        var logoFileName = Path.GetFileNameWithoutExtension(exactPath);
        if (string.IsNullOrWhiteSpace(logoDirectory) || string.IsNullOrWhiteSpace(logoFileName) || !Directory.Exists(logoDirectory))
        {
            return null;
        }

        return Directory
            .EnumerateFiles(logoDirectory, $"{logoFileName}.scale-*.png")
            .Concat(Directory.EnumerateFiles(logoDirectory, $"{logoFileName}.targetsize-*.png"))
            .FirstOrDefault();
    }

    private async Task<Bitmap?> LoadFileThumbnailAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(filePath);
            cancellationToken.ThrowIfCancellationRequested();
            using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 64);
            cancellationToken.ThrowIfCancellationRequested();
            var thumbnailIcon = await WinRtThumbnailHelper.GetBitmapAsync(thumbnail, logger);
            if (thumbnailIcon != null)
            {
                return thumbnailIcon;
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "无法读取文件缩略图：{FilePath}", filePath);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return WindowsShellIconHelper.GetFileIcon(filePath, logger);
    }

    private static async Task<Bitmap?> LoadBitmapFileAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await using var stream = File.OpenRead(filePath);
        return new Bitmap(stream);
    }

    private static string ResolvePackageDisplayName(Package package, string sourceApp)
    {
        if (!string.IsNullOrWhiteSpace(package.DisplayName) &&
            !package.DisplayName.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
        {
            return package.DisplayName;
        }

        return sourceApp;
    }

    private static string ResolveExecutableDisplayName(string processPath, string sourceApp)
    {
        try
        {
            var fileVersionInfo = FileVersionInfo.GetVersionInfo(processPath);
            if (!string.IsNullOrWhiteSpace(fileVersionInfo.FileDescription))
            {
                return fileVersionInfo.FileDescription;
            }
        }
        catch
        {
            // File version metadata is optional.
        }

        return Path.GetFileNameWithoutExtension(processPath) ?? sourceApp;
    }

    /// <summary>
    /// 两阶段查找进程路径：先廉价筛出候选，再对少量候选读完整路径。
    /// </summary>
    /// <remarks>
    /// 此前对 ~300 个进程逐个调用 MainModule/QueryFullProcessImageName（每次都要开句柄，
    /// 失败时还要抛异常），并逐个访问 MainWindowHandle（每次全量 EnumWindows），
    /// 累计数百毫秒，切歌与打开设置页时会明显卡顿。
    ///
    /// 候选取「进程名匹配变体」或「拥有可见窗口」两类：前者覆盖 cloudmusic、
    /// lx-music-desktop 这种进程名自带标识的应用；后者对齐 1.0.8.0 的行为，
    /// 覆盖进程名不含标识、只有安装路径含标识的应用。两者合计通常不超过数十个。
    /// </remarks>
    private static string? TryFindProcessPath(string sourceApp)
    {
        var variants = MediaSourceProcessMatcher.GetIdentifierVariants(sourceApp);
        if (variants.Count == 0)
        {
            return null;
        }

        // 阶段一：只读进程名与预先收集的窗口 PID 集合，不开任何进程句柄。
        var windowedPids = WindowsProcessPathHelper.GetProcessIdsWithVisibleWindow();
        var candidates = new List<(Process Process, bool HasWindow)>();
        foreach (var process in Process.GetProcesses())
        {
            var hasWindow = windowedPids.Contains(process.Id);
            var nameMatched = MediaSourceProcessMatcher.ScoreProcessName(
                sourceApp, process.ProcessName, hasWindow, variants) > 0;
            if (nameMatched || hasWindow)
            {
                candidates.Add((process, hasWindow));
            }
            else
            {
                process.Dispose();
            }
        }

        // 阶段二：仅对候选读完整路径，取综合分最高者。
        string? bestPath = null;
        var bestScore = 0;
        foreach (var (process, hasWindow) in candidates)
        {
            using (process)
            {
                var path = WindowsProcessPathHelper.TryGetExecutablePath(process);
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    continue;
                }

                var score = MediaSourceProcessMatcher.ScoreCandidate(
                    sourceApp, path, hasWindow, variants);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestPath = path;
                }
            }
        }

        return bestPath;
    }

    private static bool TryGetPackageFamilyName(string sourceApp, out string packageFamilyName)
    {
        var separatorIndex = sourceApp.IndexOf('!');
        if (separatorIndex <= 0)
        {
            packageFamilyName = string.Empty;
            return false;
        }

        packageFamilyName = sourceApp[..separatorIndex];
        return true;
    }
}
