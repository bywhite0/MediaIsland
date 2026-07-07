using System.Diagnostics;
using System.IO;
using Avalonia.Media.Imaging;
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

    private static string? TryFindProcessPath(string sourceApp)
    {
        var processNames = GetCandidateProcessNames(sourceApp);
        foreach (var processName in processNames)
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    try
                    {
                        var fileName = process.MainModule?.FileName;
                        if (!string.IsNullOrWhiteSpace(fileName) && File.Exists(fileName))
                        {
                            return fileName;
                        }
                    }
                    catch
                    {
                        // Some processes do not allow module inspection.
                    }
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> GetCandidateProcessNames(string sourceApp)
    {
        var sourceName = Path.GetFileNameWithoutExtension(sourceApp);
        if (!string.IsNullOrWhiteSpace(sourceName))
        {
            yield return sourceName;
        }

        if (sourceApp.Equals("MSEdge", StringComparison.OrdinalIgnoreCase))
        {
            yield return "msedge";
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
