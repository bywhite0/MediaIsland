using System.Text.Json;
using Lyricify.Lyrics.Parsers;
using MediaIsland.Services.Lyrics.Models;
using MediaIsland.Services.Media;
using Microsoft.Extensions.Logging;

namespace MediaIsland.Services.Lyrics.Providers;

public sealed class NeteaseLyricsProvider(ILogger<NeteaseLyricsProvider>? logger = null) : ILyricsProvider
{
    private static readonly HttpClient HttpClient = LyricsHttp.CreateClient("netease");

    public LyricsSourceId Id => LyricsSourceId.Netease;

    public async Task<IReadOnlyList<LyricsCandidate>> SearchAsync(
        MediaInfo media,
        LyricsSourceSettings settings,
        CancellationToken cancellationToken)
    {
        if (!settings.IsSourceEnabled(Id))
        {
            return [];
        }

        var candidates = new List<LyricsCandidate>();
        foreach (var query in LyricsTextNormalizer.BuildSearchQueries(media.Title, media.Artist, media.AlbumTitle))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var direct = await SearchNeteaseApiAsync(query, media, cancellationToken);
            if (direct != null)
            {
                candidates.Add(direct);
                break;
            }
        }

        return candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.ProviderItemId))
            .OrderByDescending(candidate => candidate.Score)
            .Take(3)
            .ToArray();
    }

    public async Task<LyricsPayload?> FetchAsync(
        LyricsCandidate candidate,
        LyricsSourceSettings settings,
        CancellationToken cancellationToken)
    {
        if (!settings.IsSourceEnabled(Id) || string.IsNullOrWhiteSpace(candidate.ProviderItemId))
        {
            return null;
        }

        var lyrics = await GetNeteaseLyricsAsync(candidate.ProviderItemId, cancellationToken);
        if (lyrics == null || string.IsNullOrWhiteSpace(lyrics.Value.Lyric))
        {
            return null;
        }

        // Validate that LRC can be parsed lightly.
        try
        {
            _ = LrcParser.Parse(lyrics.Value.Lyric.AsSpan());
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "[Lyrics:Netease] Failed to parse LRC for {Id}", candidate.ProviderItemId);
            return null;
        }

        return new LyricsPayload(
            LyricsFormat.Lrc,
            lyrics.Value.Lyric,
            Id,
            candidate.ProviderItemId,
            new LyricsMetadata(candidate.Title, candidate.Artist, candidate.Album, candidate.Duration),
            lyrics.Value.Translation);
    }

    private async Task<LyricsCandidate?> SearchNeteaseApiAsync(
        string query,
        MediaInfo media,
        CancellationToken cancellationToken)
    {
        var url = $"https://music.163.com/api/search/get/web?s={Uri.EscapeDataString(query)}&type=1&limit=10&offset=0";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Referrer = new Uri("https://music.163.com/");

        using var response = await HttpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            LyricsHttp.LogHttpFailure(logger, "Netease", response.StatusCode);
            return null;
        }

        using var json = await LyricsHttp.ReadJsonDocumentAsync(response, LyricsHttp.DefaultMaxBytes, cancellationToken);
        if (json == null)
        {
            return null;
        }

        if (json.RootElement.ValueKind != JsonValueKind.Object ||
            !json.RootElement.TryGetProperty("result", out var result) ||
            result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("songs", out var songs) ||
            songs.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        LyricsCandidate? best = null;
        foreach (var song in songs.EnumerateArray())
        {
            if (song.ValueKind != JsonValueKind.Object ||
                !TryReadInt64Property(song, "id", out var id) ||
                !TryReadStringProperty(song, "name", out var title))
            {
                continue;
            }

            var artist = ReadArtists(song);
            var album = ReadAlbum(song);
            var duration = ReadDuration(song);
            var aliases = ReadStringArray(song, "alias")
                .Concat(ReadStringArray(song, "transNames"))
                .ToArray();
            var score = LyricsCandidateScorer.Score(media, title, artist, album, duration, aliases);
            var candidate = new LyricsCandidate(
                Id,
                id.ToString(),
                title,
                artist,
                album,
                duration,
                score,
                SupportsWordSync: false);
            if (best == null || candidate.Score > best.Score)
            {
                best = candidate;
            }
        }

        if (best == null || best.Score < LyricsCandidateScorer.MinimumScore(media))
        {
            return null;
        }

        return best;
    }

    private async Task<(string Lyric, string? Translation)?> GetNeteaseLyricsAsync(
        string id,
        CancellationToken cancellationToken)
    {
        var url =
            $"https://music.163.com/api/song/lyric?id={Uri.EscapeDataString(id)}&lv=-1&kv=-1&tv=-1";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Referrer = new Uri("https://music.163.com/");
        using var response = await HttpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            LyricsHttp.LogHttpFailure(logger, "Netease", response.StatusCode, "lyric");
            return null;
        }

        var body = await LyricsHttp.ReadBoundedStringAsync(response, LyricsHttp.DefaultMaxBytes, cancellationToken);
        return ParseLyricResponseBody(body);
    }

    internal static (string Lyric, string? Translation)? ParseLyricResponseBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        using var document = JsonDocument.Parse(body);
        var lyric = ReadNestedLyric(document.RootElement, "lrc");
        if (string.IsNullOrWhiteSpace(lyric))
        {
            return null;
        }

        var translation = ReadNestedLyric(document.RootElement, "tlyric");
        return (lyric, string.IsNullOrWhiteSpace(translation) ? null : translation);
    }

    private static string? ReadNestedLyric(JsonElement root, string propertyName)
    {
        return root.ValueKind == JsonValueKind.Object &&
               root.TryGetProperty(propertyName, out var container) &&
               container.ValueKind == JsonValueKind.Object &&
               container.TryGetProperty("lyric", out var lyric) &&
               lyric.ValueKind == JsonValueKind.String
            ? lyric.GetString()
            : null;
    }

    private static string ReadArtists(JsonElement song)
    {
        if (song.ValueKind != JsonValueKind.Object ||
            !song.TryGetProperty("artists", out var artists) ||
            artists.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        return string.Join(
            " ",
            artists.EnumerateArray()
                .Select(artist => TryReadStringProperty(artist, "name", out var name) ? name : null)
                .Where(name => !string.IsNullOrWhiteSpace(name)));
    }

    private static string ReadAlbum(JsonElement song)
    {
        return song.ValueKind == JsonValueKind.Object &&
               song.TryGetProperty("album", out var album) &&
               TryReadStringProperty(album, "name", out var name)
            ? name
            : string.Empty;
    }

    private static TimeSpan ReadDuration(JsonElement song)
    {
        return TryReadInt64Property(song, "duration", out var duration)
            ? TimeSpan.FromMilliseconds(duration)
            : TimeSpan.Zero;
    }

    private static IEnumerable<string> ReadStringArray(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var value in property.EnumerateArray())
        {
            var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            if (!string.IsNullOrWhiteSpace(text))
            {
                yield return text;
            }
        }
    }

    private static bool TryReadInt64Property(JsonElement element, string propertyName, out long value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt64(out value);
    }

    private static bool TryReadStringProperty(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }
}
