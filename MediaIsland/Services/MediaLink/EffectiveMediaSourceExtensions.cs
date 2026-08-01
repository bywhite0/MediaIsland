using MediaIsland.Models;
using MediaIsland.Services.Media;

namespace MediaIsland.Services.MediaLink;

internal static class EffectiveMediaSourceExtensions
{
    /// <summary>
    /// Gets the UI media without replacing an intentional effective null with platform media.
    /// </summary>
    public static MediaInfo? GetCurrentUiMediaInfo(
        this IEffectiveMediaSource? effectiveSource,
        IMediaService mediaService)
    {
        ArgumentNullException.ThrowIfNull(mediaService);

        return effectiveSource is null
            ? mediaService.CurrentMediaInfo
            : effectiveSource.EffectiveMediaInfo;
    }
}
