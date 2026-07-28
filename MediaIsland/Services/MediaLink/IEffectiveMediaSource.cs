using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Media;

namespace MediaIsland.Services.MediaLink;

public interface IEffectiveMediaSource
{
    MediaInfo? EffectiveMediaInfo { get; }
    LyricsSearchResult? EffectiveLyrics { get; }

    /// <summary>
    /// True when the composed media for the current source mode comes from external inject.
    /// </summary>
    bool IsExternalMediaEffective { get; }

    event EventHandler<MediaInfoChangedEventArgs>? EffectiveMediaChanged;
    event EventHandler<LyricsSearchResultChangedEventArgs>? EffectiveLyricsChanged;
}
