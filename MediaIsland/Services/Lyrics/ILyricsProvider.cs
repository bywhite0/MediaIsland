using MediaIsland.Services.Lyrics.Models;
using MediaIsland.Services.Media;

namespace MediaIsland.Services.Lyrics;

public interface ILyricsProvider
{
    LyricsSourceId Id { get; }

    Task<IReadOnlyList<LyricsCandidate>> SearchAsync(
        MediaInfo media,
        LyricsSourceSettings settings,
        CancellationToken cancellationToken);

    Task<LyricsPayload?> FetchAsync(
        LyricsCandidate candidate,
        LyricsSourceSettings settings,
        CancellationToken cancellationToken);
}

public interface ILyricsPayloadParser
{
    bool CanParse(LyricsFormat format);

    ValueTask<LyricsDocument> ParseAsync(
        LyricsPayload payload,
        CancellationToken cancellationToken);
}
