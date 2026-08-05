using System.Collections.Concurrent;
using System.IO;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MediaIsland.Models;
using MediaIsland.Services.Media.Platform;
using Microsoft.Extensions.Logging;

namespace MediaIsland.Services.Media.SourceDisplay;

public sealed class MediaSourceDisplayService(
    Plugin plugin,
    MediaPlatformProviderResolver providerResolver,
    ILogger<MediaSourceDisplayService> logger) : IMediaSourceDisplayService
{
    private static readonly IReadOnlyDictionary<string, string> DisplayNameMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["cloudmusic"] = "网易云音乐",
            ["cloudmusic.exe"] = "网易云音乐",
            ["cn.toside.music.desktop"] = "LX Music",
            ["net.stevexmh.amllplayer"] = "AMLL Player",
            ["spotify.exe"] = "Spotify",
            ["SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify"] = "Spotify",
            ["MSEdge"] = "Microsoft Edge",
            ["msedge.exe"] = "Microsoft Edge"
        };

    private static readonly IReadOnlyDictionary<string, Uri> BundledIconMap =
        new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase)
        {
            ["cn.toside.music.desktop"] = new("avares://MediaIsland/Assets/SourceAppIcons/cn.toside.music.desktop.png"),
            ["net.stevexmh.amllplayer"] = new("avares://MediaIsland/Assets/SourceAppIcons/net.stevexmh.amllplayer.png")
        };

    private readonly ConcurrentDictionary<CacheKey, Task<MediaSourceDisplayInfo>> _cache = new();

    public Task<MediaSourceDisplayInfo> ResolveAsync(
        string sourceApp,
        CancellationToken cancellationToken = default)
    {
        var source = FindMediaSource(sourceApp);
        var iconPath = source?.IconPath;
        var customDisplayName = source?.CustomDisplayName;
        var cacheKey = new CacheKey(sourceApp, iconPath, customDisplayName);
        return _cache.GetOrAdd(
            cacheKey,
            _ => ResolveCoreAsync(sourceApp, iconPath, customDisplayName, cancellationToken));
    }

    public void Invalidate(string sourceApp)
    {
        foreach (var key in _cache.Keys.Where(key => string.Equals(key.SourceApp, sourceApp, StringComparison.OrdinalIgnoreCase)))
        {
            _cache.TryRemove(key, out _);
        }
    }

    private async Task<MediaSourceDisplayInfo> ResolveCoreAsync(
        string sourceApp,
        string? iconPath,
        string? customDisplayName,
        CancellationToken cancellationToken)
    {
        var mapping = ResolveMappedDisplayName(sourceApp);
        var displayName = MediaSourceDisplayNameResolver.Resolve(mapping, customDisplayName);

        if (!string.IsNullOrWhiteSpace(iconPath))
        {
            var userIcon = LoadFileIcon(iconPath);
            if (userIcon != null)
            {
                return new MediaSourceDisplayInfo(sourceApp, displayName, userIcon, MediaSourceDisplayKind.UserConfigured);
            }
        }

        var platformInfo = await ResolvePlatformInfoAsync(sourceApp, cancellationToken);
        displayName = MediaSourceDisplayNameResolver.Resolve(
            mapping,
            customDisplayName,
            platformInfo?.DisplayName);
        if (platformInfo is { Icon: not null })
        {
            return new MediaSourceDisplayInfo(
                sourceApp,
                displayName,
                platformInfo.Icon,
                MediaSourceDisplayKind.Platform);
        }

        var bundledIcon = LoadBundledIcon(sourceApp);
        if (bundledIcon != null)
        {
            return new MediaSourceDisplayInfo(
                sourceApp,
                displayName,
                bundledIcon,
                MediaSourceDisplayKind.Bundled);
        }

        return new MediaSourceDisplayInfo(
            sourceApp,
            displayName,
            null,
            displayName == sourceApp ? MediaSourceDisplayKind.Unknown : MediaSourceDisplayKind.Mapping);
    }

    private async Task<MediaSourceInfo?> ResolvePlatformInfoAsync(
        string sourceApp,
        CancellationToken cancellationToken)
    {
        try
        {
            return await providerResolver.Resolve().SourceInfoProvider.ResolveAsync(sourceApp, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to resolve media source info for {SourceApp}.", sourceApp);
            return null;
        }
    }

    private Bitmap? LoadFileIcon(string iconPath)
    {
        try
        {
            if (!File.Exists(iconPath))
            {
                return null;
            }

            using var stream = File.OpenRead(iconPath);
            return new Bitmap(stream);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to load configured media source icon: {IconPath}", iconPath);
            return null;
        }
    }

    private Bitmap? LoadBundledIcon(string sourceApp)
    {
        try
        {
            if (!BundledIconMap.TryGetValue(sourceApp, out var iconUri))
            {
                return null;
            }

            using var stream = AssetLoader.Open(iconUri);
            return new Bitmap(stream);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to load bundled media source icon for {SourceApp}.", sourceApp);
            return null;
        }
    }

    private MediaSource? FindMediaSource(string sourceApp)
    {
        return plugin.Settings.MediaSourceList.FirstOrDefault(source =>
            source != null &&
            string.Equals(source.Source, sourceApp, StringComparison.OrdinalIgnoreCase));
    }

    private static MappedDisplayName ResolveMappedDisplayName(string sourceApp)
    {
        if (DisplayNameMap.TryGetValue(sourceApp, out var displayName))
        {
            return new MappedDisplayName(displayName, true);
        }

        if (sourceApp.Contains("cloudmusic", StringComparison.OrdinalIgnoreCase))
        {
            return new MappedDisplayName("网易云音乐", true);
        }

        if (sourceApp.Contains("spotify", StringComparison.OrdinalIgnoreCase))
        {
            return new MappedDisplayName("Spotify", true);
        }

        // .exe 形式的 AUMID 去掉扩展名只是兜底，进程自身的描述名更准确，不算显式映射。
        if (sourceApp.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return new MappedDisplayName(Path.GetFileNameWithoutExtension(sourceApp), false);
        }

        return new MappedDisplayName(sourceApp, false);
    }

    private sealed record CacheKey(string SourceApp, string? IconPath, string? CustomDisplayName);
}

/// <param name="IsExplicit">是否命中内置映射表；命中时应覆盖平台返回的名称。</param>
internal sealed record MappedDisplayName(string Value, bool IsExplicit);

internal static class MediaSourceDisplayNameResolver
{
    internal static string Resolve(
        MappedDisplayName mapping,
        string? customDisplayName,
        string? platformDisplayName = null)
    {
        // 用户自定义 > 内置映射 > 平台解析名 > AUMID 兜底。
        if (!string.IsNullOrWhiteSpace(customDisplayName))
        {
            return customDisplayName;
        }

        if (mapping.IsExplicit)
        {
            return mapping.Value;
        }

        return string.IsNullOrWhiteSpace(platformDisplayName)
            ? mapping.Value
            : platformDisplayName;
    }
}
