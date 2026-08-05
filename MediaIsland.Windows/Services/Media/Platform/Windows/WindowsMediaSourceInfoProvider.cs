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
    private static readonly TimeSpan ProcessCacheDuration = TimeSpan.FromSeconds(5);
    private static readonly object ProcessCacheGate = new();
    private static Process[]? _cachedProcesses;
    private static DateTime _processCacheTimestamp = DateTime.MinValue;

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
        var processPath = TryFindProcessPath(sourceApp);
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
    /// 扫描全部进程，用 AUMID 变体模糊匹配可执行文件路径，取分数最高者。
    /// </summary>
    /// <remarks>
    /// 不能只用 <see cref="Process.GetProcessesByName"/> 精确匹配进程名：
    /// <c>cn.toside.music.desktop</c> 经 <see cref="Path.GetFileNameWithoutExtension"/> 会变成
    /// <c>cn.toside.music</c>，永远匹配不到实际进程 <c>lx-music-desktop</c>。
    /// </remarks>
    private static string? TryFindProcessPath(string sourceApp)
    {
        var variants = MediaSourceProcessMatcher.GetIdentifierVariants(sourceApp);
        if (variants.Count == 0)
        {
            return null;
        }

        string? bestPath = null;
        var bestScore = 0;
        foreach (var process in GetCachedProcesses())
        {
            var candidate = TryReadProcessPath(process);
            if (candidate == null)
            {
                continue;
            }

            var score = MediaSourceProcessMatcher.ScoreCandidate(
                sourceApp,
                candidate.Value.Path,
                candidate.Value.HasMainWindow,
                variants);
            if (score > bestScore)
            {
                bestScore = score;
                bestPath = candidate.Value.Path;
            }
        }

        return bestPath;
    }

    private static (string Path, bool HasMainWindow)? TryReadProcessPath(Process process)
    {
        try
        {
            var fileName = WindowsProcessPathHelper.TryGetExecutablePath(process);
            if (string.IsNullOrWhiteSpace(fileName) || !File.Exists(fileName))
            {
                return null;
            }

            return (fileName, process.MainWindowHandle != IntPtr.Zero);
        }
        catch
        {
            // 受保护或已退出的进程读不到信息，跳过即可。
            return null;
        }
    }

    /// <summary>
    /// 缓存进程快照，避免每次切歌都全量枚举进程。
    /// </summary>
    private static IReadOnlyList<Process> GetCachedProcesses()
    {
        lock (ProcessCacheGate)
        {
            if (_cachedProcesses != null &&
                DateTime.UtcNow - _processCacheTimestamp < ProcessCacheDuration)
            {
                return _cachedProcesses;
            }

            if (_cachedProcesses != null)
            {
                foreach (var process in _cachedProcesses)
                {
                    process.Dispose();
                }
            }

            _cachedProcesses = Process.GetProcesses();
            _processCacheTimestamp = DateTime.UtcNow;
            return _cachedProcesses;
        }
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
