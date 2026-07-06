using System.Net.Http;
using System.Text.Json;
using Lyricify.Lyrics.Models;
using Lyricify.Lyrics.Parsers;
using Lyricify.Lyrics.Providers.Web.Netease;
using Lyricify.Lyrics.Searchers;
using MediaIsland.Models;
using Microsoft.Extensions.Logging;

namespace MediaIsland.Services;

public sealed class LyricsSearchService(ILogger? logger = null)
{
    private static readonly HttpClient NeteaseHttpClient = new();

    private readonly NeteaseSearcher _searcher = new();

    public async Task<LyricsSearchResult?> SearchAsync(MediaInfo info, CancellationToken cancellationToken = default)
    {
        try
        {
            foreach (var query in BuildSearchQueries(info))
            {
                cancellationToken.ThrowIfCancellationRequested();
                logger?.LogInformation("[Lyrics] Searching: {Query}", query);
                var directResult = await SearchNeteaseApiAsync(query, info, cancellationToken);
                if (directResult != null)
                {
                    logger?.LogInformation(
                        "[Lyrics] Found: {Title} - {Artist} ({Duration}) - {Id}, score {Score}",
                        directResult.Title,
                        directResult.Artist,
                        directResult.Duration,
                        directResult.Id,
                        directResult.Score);

                    var directLyrics = await TryParseNeteaseLyricsAsync(directResult.Id, cancellationToken);
                    if (directLyrics == null)
                    {
                        logger?.LogInformation("[Lyrics] Start parsing failed: No text found in response.");
                        continue;
                    }

                    return new LyricsSearchResult(
                        directLyrics,
                        directResult.Id.ToString(),
                        directResult.Title,
                        directResult.Artist,
                        directResult.Duration,
                        directResult.Score,
                        LyricsSearchSource.DirectNeteaseApi);
                }

                if (info.Duration > TimeSpan.Zero)
                {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                var searchResults = await _searcher.SearchForResults(query);
                cancellationToken.ThrowIfCancellationRequested();
                var result = searchResults?.OfType<NeteaseSearchResult>().FirstOrDefault();
                if (result == null || !IsTitleLikelyMatch(info.Title, result.Title))
                {
                    continue;
                }

                logger?.LogInformation("[Lyrics] Found by package fallback: {Title} - {Id}", result.Title, result.Id);
                var packageLyrics = await TryParseNeteaseLyricsAsync(result.Id, cancellationToken);
                if (packageLyrics == null)
                {
                    logger?.LogInformation("[Lyrics] Start parsing failed: No text found in response.");
                    continue;
                }

                return new LyricsSearchResult(
                    packageLyrics,
                    result.Id.ToString() ?? string.Empty,
                    result.Title,
                    string.Empty,
                    TimeSpan.Zero,
                    null,
                    LyricsSearchSource.PackageFallback);
            }

            logger?.LogInformation("[Lyrics] Not found.");
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "[Lyrics] Error while searching lyrics.");
            return null;
        }
    }

    private static async Task<DirectNeteaseSearchResult?> SearchNeteaseApiAsync(
        string query,
        MediaInfo info,
        CancellationToken cancellationToken)
    {
        var url = $"https://music.163.com/api/search/get/web?s={Uri.EscapeDataString(query)}&type=1&limit=10&offset=0";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0");
        request.Headers.Referrer = new Uri("https://music.163.com/");

        using var response = await NeteaseHttpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!json.RootElement.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("songs", out var songs) ||
            songs.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        DirectNeteaseSearchResult? bestResult = null;
        foreach (var song in songs.EnumerateArray())
        {
            if (!song.TryGetProperty("id", out var idElement) ||
                !idElement.TryGetInt64(out var id) ||
                !song.TryGetProperty("name", out var nameElement))
            {
                continue;
            }

            var title = nameElement.GetString();
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var artist = ReadArtists(song);
            var album = ReadAlbum(song);
            var duration = ReadDuration(song);
            var aliases = ReadStringArray(song, "alias")
                .Concat(ReadStringArray(song, "transNames"))
                .ToArray();
            var score = ScoreNeteaseResult(info, title, aliases, artist, album, duration);
            var candidate = new DirectNeteaseSearchResult(id, title, artist, album, duration, score);
            if (bestResult == null || candidate.Score > bestResult.Score)
            {
                bestResult = candidate;
            }
        }

        if (bestResult == null)
        {
            return null;
        }

        var minimumScore = info.Duration > TimeSpan.Zero ? 80 : 60;
        if (bestResult.Score < minimumScore)
        {
            return null;
        }

        return bestResult;
    }

    private static int ScoreNeteaseResult(
        MediaInfo info,
        string title,
        IEnumerable<string> aliases,
        string artist,
        string album,
        TimeSpan duration)
    {
        var score = 0;
        var infoTitle = NormalizeComparableText(info.Title);
        var infoArtist = NormalizeComparableText(info.Artist);
        var infoAlbum = NormalizeComparableText(info.AlbumTitle);
        var candidateTitles = aliases
            .Append(title)
            .Select(NormalizeComparableText)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var candidateTitle = NormalizeComparableText(title);
        var candidateArtist = NormalizeComparableText(artist);
        var candidateAlbum = NormalizeComparableText(album);

        if (candidateTitles.Any(value => value.Equals(infoTitle, StringComparison.OrdinalIgnoreCase)))
        {
            score += 80;
        }
        else if (candidateTitles.Any(value => value.Contains(infoTitle, StringComparison.OrdinalIgnoreCase) ||
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
            if (candidateArtist.Equals(infoArtist, StringComparison.OrdinalIgnoreCase))
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

        if (info.Duration > TimeSpan.Zero && duration > TimeSpan.Zero)
        {
            var durationDiff = Math.Abs((info.Duration - duration).TotalSeconds);
            score += durationDiff switch
            {
                <= 2 => 45,
                <= 5 => 35,
                <= 10 => 25,
                <= 20 => 10,
                _ => -80
            };
        }

        if (!LooksLikeSameVersion(infoTitle, candidateTitle))
        {
            score -= 25;
        }

        return score;
    }

    private static bool LooksLikeSameVersion(string infoTitle, string candidateTitle)
    {
        var versionWords = new[] { "live", "remix", "cover", "instrumental", "karaoke", "伴奏", "纯音乐" };
        foreach (var word in versionWords)
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

    private static bool IsTitleLikelyMatch(string expectedTitle, string candidateTitle)
    {
        var expected = NormalizeComparableText(expectedTitle);
        var candidate = NormalizeComparableText(candidateTitle);
        return !string.IsNullOrWhiteSpace(expected) &&
               (candidate.Equals(expected, StringComparison.OrdinalIgnoreCase) ||
                candidate.Contains(expected, StringComparison.OrdinalIgnoreCase) ||
                expected.Contains(candidate, StringComparison.OrdinalIgnoreCase));
    }

    private static string ReadArtists(JsonElement song)
    {
        if (!song.TryGetProperty("artists", out var artists) ||
            artists.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        return string.Join(
            " ",
            artists.EnumerateArray()
                .Select(artist => artist.TryGetProperty("name", out var name) ? name.GetString() : null)
                .Where(name => !string.IsNullOrWhiteSpace(name)));
    }

    private static string ReadAlbum(JsonElement song)
    {
        return song.TryGetProperty("album", out var album) &&
               album.TryGetProperty("name", out var name)
            ? name.GetString() ?? string.Empty
            : string.Empty;
    }

    private static TimeSpan ReadDuration(JsonElement song)
    {
        return song.TryGetProperty("duration", out var durationElement) &&
               durationElement.TryGetInt64(out var duration)
            ? TimeSpan.FromMilliseconds(duration)
            : TimeSpan.Zero;
    }

    private static IEnumerable<string> ReadStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var value in property.EnumerateArray())
        {
            var text = value.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                yield return text;
            }
        }
    }

    private static IEnumerable<string> BuildSearchQueries(MediaInfo info)
    {
        var title = NormalizeSearchText(info.Title);
        var artist = NormalizeSearchText(info.Artist);
        var album = NormalizeSearchText(info.AlbumTitle);

        if (string.IsNullOrWhiteSpace(title))
        {
            yield break;
        }

        var queries = new[]
        {
            string.IsNullOrWhiteSpace(artist) ? null : $"{title} - {artist}",
            string.IsNullOrWhiteSpace(artist) ? null : $"{title} {artist}",
            string.IsNullOrWhiteSpace(artist) ? null : $"{artist} - {title} {album}",
            string.IsNullOrWhiteSpace(album) ? null : $"{title} {album}",
            title
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

    private static string NormalizeSearchText(string? text)
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

    private static string NormalizeComparableText(string? text)
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

    private static async Task<LyricsData?> TryParseNeteaseLyricsAsync(object id, CancellationToken cancellationToken)
    {
        var lyricsString = await GetNeteaseLyricsAsync(id, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return string.IsNullOrWhiteSpace(lyricsString)
            ? null
            : LrcParser.Parse(lyricsString.AsSpan());
    }

    private static async Task<string?> GetNeteaseLyricsAsync(object id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        dynamic api = new Api();
        dynamic response = await api.GetLyric(id.ToString());
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            string? lyric = response?.Lrc?.Lyric;
            if (!string.IsNullOrWhiteSpace(lyric))
            {
                return lyric;
            }
        }
        catch
        {
            // Different provider versions expose different response shapes.
        }

        try
        {
            string? lyric = response?.Lyric;
            if (!string.IsNullOrWhiteSpace(lyric))
            {
                return lyric;
            }
        }
        catch
        {
            // ignored
        }

        return null;
    }

    private sealed record DirectNeteaseSearchResult(
        long Id,
        string Title,
        string Artist,
        string Album,
        TimeSpan Duration,
        int Score);
}

public sealed record LyricsSearchResult(
    LyricsData Lyrics,
    string Id,
    string Title,
    string Artist,
    TimeSpan Duration,
    int? Score,
    LyricsSearchSource Source);

public enum LyricsSearchSource
{
    DirectNeteaseApi,
    PackageFallback
}
