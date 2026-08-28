using MediaIsland.Models;
using MediaIsland.Services.Lyrics;
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

    /// <summary>
    /// Gets the lyrics the UI should present. Without a coordinator this reads the local
    /// search result; with one it defers to GetLyricsForUi, which owns the
    /// MediaLinkUiUsesEffective branch so pages never re-decide it.
    /// </summary>
    public static LyricsSearchResult? GetCurrentUiLyrics(
        this IEffectiveMediaSource? effectiveSource,
        LyricsSearchService lyricsSearchService,
        IMediaService mediaService)
    {
        ArgumentNullException.ThrowIfNull(lyricsSearchService);
        ArgumentNullException.ThrowIfNull(mediaService);

        return effectiveSource is null
            ? lyricsSearchService.GetCurrentResultFor(mediaService.CurrentMediaInfo)
            : effectiveSource.GetLyricsForUi();
    }
}
