using System.Text.Json;
using MediaIsland.Services.Lyrics.Models;
using MediaIsland.Services.Lyrics.Parsers;
using MediaIsland.Services.Media;
using Microsoft.Extensions.Logging;

namespace MediaIsland.Services.Lyrics.Providers;

public sealed class KugouLyricsProvider(ILogger<KugouLyricsProvider>? logger = null) : ILyricsProvider
{
    private static readonly HttpClient HttpClient = LyricsHttp.CreateClient("kugou");

    public LyricsSourceId Id => LyricsSourceId.Kugou;

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
            var songs = await SearchSongsAsync(query, cancellationToken);
            foreach (var song in songs)
            {
                var score = LyricsCandidateScorer.Score(
                    media,
                    song.Title,
                    song.Artist,
                    song.Album,
                    song.Duration,
                    sourceBonus: !string.IsNullOrWhiteSpace(song.Hash) ? 5 : 0);
                if (score < LyricsCandidateScorer.MinimumScore(media))
                {
                    continue;
                }

                candidates.Add(new LyricsCandidate(
                    Id,
                    string.IsNullOrWhiteSpace(song.Hash) ? $"{song.Title}|{song.Artist}" : song.Hash,
                    song.Title,
                    song.Artist,
                    song.Album,
                    song.Duration,
                    score,
                    SupportsWordSync: true,
                    Extra: new Dictionary<string, string>
                    {
                        ["hash"] = song.Hash,
                        ["durationMs"] = ((int)song.Duration.TotalMilliseconds).ToString()
                    }));
            }

            if (candidates.Count > 0)
            {
                break;
            }
        }

        return candidates
            .OrderByDescending(candidate => candidate.Score)
            .Take(5)
            .ToArray();
    }

    public async Task<LyricsPayload?> FetchAsync(
        LyricsCandidate candidate,
        LyricsSourceSettings settings,
        CancellationToken cancellationToken)
    {
        if (!settings.IsSourceEnabled(Id))
        {
            return null;
        }

        var hash = candidate.Extra != null && candidate.Extra.TryGetValue("hash", out var hashValue)
            ? hashValue
            : candidate.ProviderItemId;
        var durationMs = 0;
        if (candidate.Extra != null &&
            candidate.Extra.TryGetValue("durationMs", out var durationText) &&
            int.TryParse(durationText, out var parsedDuration))
        {
            durationMs = parsedDuration;
        }
        else if (candidate.Duration > TimeSpan.Zero)
        {
            durationMs = (int)candidate.Duration.TotalMilliseconds;
        }

        var lyricCandidate = await SearchLyricCandidateAsync(candidate.Title, candidate.Artist, hash, durationMs, cancellationToken);
        if (lyricCandidate == null)
        {
            return null;
        }

        var encrypted = await DownloadEncryptedLyricsAsync(lyricCandidate.Id, lyricCandidate.AccessKey, cancellationToken);
        if (string.IsNullOrWhiteSpace(encrypted))
        {
            return null;
        }

        var decrypted = ManagedLyricsPayloadParser.DecryptKrc(encrypted);
        if (string.IsNullOrWhiteSpace(decrypted))
        {
            logger?.LogWarning("[Lyrics:Kugou] Failed to decrypt KRC for {Id}", lyricCandidate.Id);
            return null;
        }

        return new LyricsPayload(
            LyricsFormat.Krc,
            decrypted,
            Id,
            lyricCandidate.Id,
            new LyricsMetadata(candidate.Title, candidate.Artist, candidate.Album, candidate.Duration));
    }

    private async Task<IReadOnlyList<KugouSong>> SearchSongsAsync(string query, CancellationToken cancellationToken)
    {
        var url =
            $"https://songsearch.kugou.com/song_search_v2?keyword={Uri.EscapeDataString(query)}&page=1&pagesize=10";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await HttpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            LyricsHttp.LogHttpFailure(logger, "Kugou", response.StatusCode, "song-search");
            return [];
        }

        return await ParseSongsAsync(response, cancellationToken);
    }

    private static async Task<IReadOnlyList<KugouSong>> ParseSongsAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        using var document = await LyricsHttp.ReadJsonDocumentAsync(response, LyricsHttp.DefaultMaxBytes, cancellationToken);
        if (document == null ||
            !document.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        JsonElement info;
        if ((!data.TryGetProperty("lists", out info) || info.ValueKind != JsonValueKind.Array) &&
            (!data.TryGetProperty("info", out info) || info.ValueKind != JsonValueKind.Array))
        {
            return [];
        }

        var songs = new List<KugouSong>();
        foreach (var item in info.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var hash = ReadString(item, "FileHash", "hash");
            var title = ReadString(item, "SongName", "songname", "songname_original");
            var artist = ReadString(item, "SingerName", "singername");
            var album = ReadString(item, "AlbumName", "album_name");
            var duration = TimeSpan.Zero;
            if ((item.TryGetProperty("Duration", out var durationNode) || item.TryGetProperty("duration", out durationNode)) &&
                durationNode.TryGetInt32(out var seconds))
            {
                duration = TimeSpan.FromSeconds(seconds);
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            songs.Add(new KugouSong(hash, title, artist, album, duration));
        }

        return songs;
    }

    private static string ReadString(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var node))
            {
                continue;
            }

            if (node.ValueKind == JsonValueKind.String)
            {
                return node.GetString() ?? string.Empty;
            }

            if (node.ValueKind == JsonValueKind.Number)
            {
                return node.ToString();
            }
        }

        return string.Empty;
    }

    private async Task<KugouLyricCandidate?> SearchLyricCandidateAsync(
        string title,
        string artist,
        string hash,
        int durationMs,
        CancellationToken cancellationToken)
    {
        var keyword = string.IsNullOrWhiteSpace(artist) ? title : $"{artist} - {title}";
        var url =
            $"https://lyrics.kugou.com/search?ver=1&man=yes&client=pc&keyword={Uri.EscapeDataString(keyword)}&duration={durationMs}&hash={Uri.EscapeDataString(hash ?? string.Empty)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await HttpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            LyricsHttp.LogHttpFailure(logger, "Kugou", response.StatusCode, "lyric-search");
            return null;
        }

        using var document = await LyricsHttp.ReadJsonDocumentAsync(response, LyricsHttp.DefaultMaxBytes, cancellationToken);
        if (document == null ||
            !document.RootElement.TryGetProperty("candidates", out var candidates) ||
            candidates.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        KugouLyricCandidate? best = null;
        var bestScore = int.MinValue;
        foreach (var item in candidates.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = item.TryGetProperty("id", out var idNode) ? idNode.ToString() : string.Empty;
            var accessKey = item.TryGetProperty("accesskey", out var accessNode)
                ? accessNode.GetString() ?? string.Empty
                : string.Empty;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(accessKey))
            {
                continue;
            }

            var song = item.TryGetProperty("song", out var songNode) ? songNode.GetString() ?? string.Empty : string.Empty;
            var singer = item.TryGetProperty("singer", out var singerNode) ? singerNode.GetString() ?? string.Empty : string.Empty;
            var duration = item.TryGetProperty("duration", out var durationNode) && durationNode.TryGetInt32(out var ms)
                ? ms
                : 0;

            var score = 0;
            if (!string.IsNullOrWhiteSpace(hash) &&
                item.TryGetProperty("product_from", out _))
            {
                score += 5;
            }

            if (!string.IsNullOrWhiteSpace(hash))
            {
                score += 20;
            }

            if (durationMs > 0 && duration > 0)
            {
                var diff = Math.Abs(durationMs - duration);
                score += diff switch
                {
                    <= 1000 => 40,
                    <= 3000 => 30,
                    <= 8000 => 15,
                    _ => -20
                };
            }

            if (!string.IsNullOrWhiteSpace(title) &&
                song.Contains(title, StringComparison.OrdinalIgnoreCase))
            {
                score += 20;
            }

            if (!string.IsNullOrWhiteSpace(artist) &&
                singer.Contains(artist, StringComparison.OrdinalIgnoreCase))
            {
                score += 15;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = new KugouLyricCandidate(id, accessKey);
            }
        }

        return best;
    }

    private async Task<string?> DownloadEncryptedLyricsAsync(
        string id,
        string accessKey,
        CancellationToken cancellationToken)
    {
        var url =
            $"https://lyrics.kugou.com/download?ver=1&client=pc&id={Uri.EscapeDataString(id)}&accesskey={Uri.EscapeDataString(accessKey)}&fmt=krc&charset=utf8";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await HttpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            LyricsHttp.LogHttpFailure(logger, "Kugou", response.StatusCode, "lyric-download");
            return null;
        }

        using var document = await LyricsHttp.ReadJsonDocumentAsync(response, LyricsHttp.DefaultMaxBytes, cancellationToken);
        if (document == null)
        {
            return null;
        }

        if (document.RootElement.TryGetProperty("content", out var contentNode))
        {
            return contentNode.GetString();
        }

        return null;
    }

    private sealed record KugouSong(string Hash, string Title, string Artist, string Album, TimeSpan Duration);
    private sealed record KugouLyricCandidate(string Id, string AccessKey);
}
