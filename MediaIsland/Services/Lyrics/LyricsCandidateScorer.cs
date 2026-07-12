using MediaIsland.Services.Media;

namespace MediaIsland.Services.Lyrics;

public static class LyricsCandidateScorer
{
    private static readonly string[] VersionWords =
    [
        "live", "remix", "cover", "instrumental", "karaoke", "伴奏", "纯音乐"
    ];

    public static int Score(
        MediaInfo media,
        string title,
        string artist,
        string album,
        TimeSpan duration,
        IEnumerable<string>? aliases = null,
        int sourceBonus = 0)
    {
        var score = sourceBonus;
        var infoTitle = LyricsTextNormalizer.NormalizeComparableText(media.Title);
        var infoArtist = LyricsTextNormalizer.NormalizeComparableText(media.Artist);
        var infoAlbum = LyricsTextNormalizer.NormalizeComparableText(media.AlbumTitle);
        var candidateTitles = (aliases ?? [])
            .Append(title)
            .Select(LyricsTextNormalizer.NormalizeComparableText)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var candidateTitle = LyricsTextNormalizer.NormalizeComparableText(title);
        var candidateArtist = LyricsTextNormalizer.NormalizeComparableText(artist);
        var candidateAlbum = LyricsTextNormalizer.NormalizeComparableText(album);

        if (candidateTitles.Any(value => value.Equals(infoTitle, StringComparison.OrdinalIgnoreCase)))
        {
            score += 80;
        }
        else if (candidateTitles.Any(value =>
                     value.Contains(infoTitle, StringComparison.OrdinalIgnoreCase) ||
                     infoTitle.Contains(value, StringComparison.OrdinalIgnoreCase)))
        {
            score += 45;
        }
        else
        {
            score -= 70;
        }

        if (!string.IsNullOrWhiteSpace(infoArtist) && !string.IsNullOrWhiteSpace(candidateArtist))
        {
            var infoArtists = LyricsTextNormalizer.SplitArtists(media.Artist);
            var candidateArtists = LyricsTextNormalizer.SplitArtists(artist);
            if (infoArtists.Any(a => candidateArtists.Any(b =>
                    a.Equals(b, StringComparison.OrdinalIgnoreCase) ||
                    a.Contains(b, StringComparison.OrdinalIgnoreCase) ||
                    b.Contains(a, StringComparison.OrdinalIgnoreCase))))
            {
                score += candidateArtists.Any(a => infoArtists.Any(b => a.Equals(b, StringComparison.OrdinalIgnoreCase)))
                    ? 35
                    : 22;
            }
            else if (candidateArtist.Equals(infoArtist, StringComparison.OrdinalIgnoreCase))
            {
                score += 35;
            }
            else if (candidateArtist.Contains(infoArtist, StringComparison.OrdinalIgnoreCase) ||
                     infoArtist.Contains(candidateArtist, StringComparison.OrdinalIgnoreCase))
            {
                score += 22;
            }
        }

        if (!string.IsNullOrWhiteSpace(infoAlbum) && !string.IsNullOrWhiteSpace(candidateAlbum))
        {
            if (candidateAlbum.Equals(infoAlbum, StringComparison.OrdinalIgnoreCase))
            {
                score += 20;
            }
            else if (candidateAlbum.Contains(infoAlbum, StringComparison.OrdinalIgnoreCase) ||
                     infoAlbum.Contains(candidateAlbum, StringComparison.OrdinalIgnoreCase))
            {
                score += 12;
            }
        }

        if (media.Duration > TimeSpan.Zero && duration > TimeSpan.Zero)
        {
            var durationDiff = Math.Abs((media.Duration - duration).TotalSeconds);
            score += durationDiff switch
            {
                <= 2 => 45,
                <= 5 => 35,
                <= 10 => 25,
                <= 20 => 10,
                <= 45 => -10,
                _ => -35
            };
        }

        if (!LooksLikeSameVersion(infoTitle, candidateTitle))
        {
            score -= 25;
        }

        return score;
    }

    public static int MinimumScore(MediaInfo media) => media.Duration > TimeSpan.Zero ? 80 : 60;

    private static bool LooksLikeSameVersion(string infoTitle, string candidateTitle)
    {
        foreach (var word in VersionWords)
        {
            var infoHasWord = infoTitle.Contains(word, StringComparison.OrdinalIgnoreCase);
            var candidateHasWord = candidateTitle.Contains(word, StringComparison.OrdinalIgnoreCase);
            if (infoHasWord != candidateHasWord)
            {
                return false;
            }
        }

        return true;
    }
}
