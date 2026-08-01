using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Media;

namespace MediaIsland.Services.MediaLink;

public interface IEffectiveMediaSource
{
    /// <summary>
    /// Gets the media selected for UI. A null result is a valid selection and must not fall back to platform media.
    /// </summary>
    MediaInfo? EffectiveMediaInfo { get; }
    LyricsSearchResult? EffectiveLyrics { get; }

    /// <summary>
    /// True when the composed media for the current source mode comes from external inject.
    /// </summary>
    bool IsExternalMediaEffective { get; }

    event EventHandler<MediaInfoChangedEventArgs>? EffectiveMediaChanged;
    event EventHandler<LyricsSearchResultChangedEventArgs>? EffectiveLyricsChanged;
}
