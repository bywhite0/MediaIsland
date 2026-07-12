using System.Text.RegularExpressions;

namespace MediaIsland.Services.Lyrics;

internal static partial class LyricsTextNormalizer
{
    public static string NormalizeSearchText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return string.Join(
            " ",
            text.Replace('/', ' ')
                .Replace('\\', ' ')
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    public static string NormalizeComparableText(string? text)
    {
        return NormalizeSearchText(NormalizeSearchText(text)
            .Replace("　", " ")
            .Replace("・", " ")
            .Replace("-", " ")
            .Replace("_", " ")
            .Replace("(", " ")
            .Replace(")", " ")
            .Replace("[", " ")
            .Replace("]", " "));
    }

    public static IEnumerable<string> BuildSearchQueries(string? title, string? artist, string? album)
    {
        var normalizedTitle = NormalizeSearchText(title);
        var normalizedArtist = NormalizeSearchText(artist);
        var normalizedAlbum = NormalizeSearchText(album);

        if (string.IsNullOrWhiteSpace(normalizedTitle))
        {
            yield break;
        }

        var queries = new[]
        {
            string.IsNullOrWhiteSpace(normalizedArtist) ? null : $"{normalizedTitle} - {normalizedArtist}",
            string.IsNullOrWhiteSpace(normalizedArtist) ? null : $"{normalizedTitle} {normalizedArtist}",
            string.IsNullOrWhiteSpace(normalizedArtist) ? null : $"{normalizedArtist} - {normalizedTitle} {normalizedAlbum}",
            string.IsNullOrWhiteSpace(normalizedAlbum) ? null : $"{normalizedTitle} {normalizedAlbum}",
            normalizedTitle
        };

        var usedQueries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var query in queries)
        {
            var normalizedQuery = NormalizeSearchText(query);
            if (!string.IsNullOrWhiteSpace(normalizedQuery) && usedQueries.Add(normalizedQuery))
            {
                yield return normalizedQuery;
            }
        }
    }

    public static IReadOnlyList<string> SplitArtists(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist))
        {
            return [];
        }

        return ArtistSplitRegex()
            .Split(artist)
            .Select(NormalizeComparableText)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    [GeneratedRegex(@"[,/&;、]| feat\.? | ft\.? | with ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArtistSplitRegex();
}
