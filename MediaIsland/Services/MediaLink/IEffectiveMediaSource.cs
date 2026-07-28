using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Media;

namespace MediaIsland.Services.MediaLink;

public interface IEffectiveMediaSource
{
    MediaInfo? EffectiveMediaInfo { get; }
    LyricsSearchResult? EffectiveLyrics { get; }

    event EventHandler<MediaInfoChangedEventArgs>? EffectiveMediaChanged;
    event EventHandler<LyricsSearchResultChangedEventArgs>? EffectiveLyricsChanged;
}
