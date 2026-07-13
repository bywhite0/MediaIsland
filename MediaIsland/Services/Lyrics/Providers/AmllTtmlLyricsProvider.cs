using System.Text.Json;
using MediaIsland.Services.Lyrics.Models;
using MediaIsland.Services.Lyrics.Native;
using MediaIsland.Services.Media;
using Microsoft.Extensions.Logging;

namespace MediaIsland.Services.Lyrics.Providers;

public sealed class AmllTtmlLyricsProvider(
    TtmlNativeParser ttmlNativeParser,
    ILogger<AmllTtmlLyricsProvider>? logger = null) : ILyricsProvider
{
    private static readonly HttpClient HttpClient = LyricsHttp.CreateClient("amll");

    public LyricsSourceId Id => LyricsSourceId.AmllTtml;

    public async Task<IReadOnlyList<LyricsCandidate>> SearchAsync(
        MediaInfo media,
        LyricsSourceSettings settings,
        CancellationToken cancellationToken)
    {
        var baseUrl = LyricsSourceSettings.NormalizeAmllBaseUrl(settings.AmllApiBaseUrl);
        if (string.IsNullOrWhiteSpace(baseUrl) || !settings.IsSourceEnabled(Id))
        {
            return [];
        }

        if (string.IsNullOrWhiteSpace(media.Title))
        {
            return [];
        }

        var candidates = new Dictionary<string, LyricsCandidate>(StringComparer.OrdinalIgnoreCase);
        var searchQueries = BuildSearchQueries(media);
        for (var attempt = 0; attempt < searchQueries.Count; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = $"{baseUrl}/api/v1/lyrics/search?{searchQueries[attempt]}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await SendWithOptionalRetryAsync(request, cancellationToken);
            if (response == null)
            {
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                LyricsHttp.LogHttpFailure(logger, "Amll", response.StatusCode, $"search-{attempt + 1}");
                continue;
            }

            using var document = await LyricsHttp.ReadJsonDocumentAsync(
                response,
                LyricsHttp.DefaultMaxBytes,
                cancellationToken);
            if (document == null || !TryGetItems(document.RootElement, out var items))
            {
                continue;
            }

            foreach (var item in items)
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var filename = ReadFirstString(item, "filename", "fileName");
                var titles = ReadStringValues(item, "musicNames", "musicName", "name");
                var artists = ReadStringValues(item, "artistNames", "artistName", "artists");
                var albums = ReadStringValues(item, "albumNames", "albumName");
                var title = titles.FirstOrDefault() ?? media.Title ?? string.Empty;
                var artist = artists.Count > 0 ? string.Join(" / ", artists) : media.Artist ?? string.Empty;
                var album = albums.FirstOrDefault() ?? media.AlbumTitle ?? string.Empty;
                if (string.IsNullOrWhiteSpace(filename))
                {
                    continue;
                }

                var score = LyricsCandidateScorer.Score(
                    media,
                    title,
                    artist,
                    album,
                    TimeSpan.Zero,
                    aliases: titles.Skip(1),
                    sourceBonus: 10);
                if (score < LyricsCandidateScorer.MinimumScore(media) - 10)
                {
                    continue;
                }

                var extra = new Dictionary<string, string>
                {
                    ["filename"] = filename
                };

                AddFirstValue(extra, "ncmMusicId", ReadStringValues(item, "ncmMusicIds", "ncmMusicId", "neteaseId", "ncmId"));
                AddFirstValue(extra, "qqMusicId", ReadStringValues(item, "qqMusicIds", "qqMusicId", "qqId"));
                AddFirstValue(extra, "appleMusicId", ReadStringValues(item, "appleMusicIds", "appleMusicId"));
                AddFirstValue(extra, "spotifyId", ReadStringValues(item, "spotifyIds", "spotifyId"));
                AddFirstValue(extra, "isrc", ReadStringValues(item, "isrcs", "isrc"));

                // 存在嵌套的平台 ID 时将其展开
                if (item.TryGetProperty("platformIds", out var platformIds) &&
                    platformIds.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in platformIds.EnumerateObject())
                    {
                        if (property.Value.ValueKind == JsonValueKind.String)
                        {
                            extra[property.Name] = property.Value.GetString() ?? string.Empty;
                        }
                        else if (property.Value.ValueKind == JsonValueKind.Number)
                        {
                            extra[property.Name] = property.Value.ToString();
                        }
                    }
                }

                candidates[filename] = new LyricsCandidate(
                    Id,
                    filename,
                    title,
                    artist,
                    album,
                    media.Duration,
                    score,
                    SupportsWordSync: true,
                    Extra: extra);
            }

            if (candidates.Count > 0)
            {
                logger?.LogInformation(
                    "[歌词:Amll] 第 {Attempt}/{Total} 次搜索成功，找到 {Count} 个候选项。",
                    attempt + 1,
                    searchQueries.Count,
                    candidates.Count);
                break;
            }
        }

        if (candidates.Count == 0)
        {
            logger?.LogInformation(
                "[歌词:Amll] 完成 {Count} 次元数据搜索后，没有找到可接受的候选项。",
                searchQueries.Count);
        }

        return candidates.Values
            .OrderByDescending(candidate => candidate.Score)
            .Take(5)
            .ToArray();
    }

    public async Task<LyricsPayload?> FetchAsync(
        LyricsCandidate candidate,
        LyricsSourceSettings settings,
        CancellationToken cancellationToken)
    {
        var baseUrl = LyricsSourceSettings.NormalizeAmllBaseUrl(settings.AmllApiBaseUrl);
        if (string.IsNullOrWhiteSpace(baseUrl) || !settings.IsSourceEnabled(Id))
        {
            return null;
        }

        if (!ttmlNativeParser.IsAvailable)
        {
            logger?.LogWarning(
                "[歌词:Amll] 原生 TTML 解析器不可用，跳过此次处理。原因：{Reason}",
                ttmlNativeParser.FailureReason);
            return null;
        }

        var filename = candidate.Extra != null && candidate.Extra.TryGetValue("filename", out var file)
            ? file
            : candidate.ProviderItemId;

        var ttml = await FetchByFilenameAsync(baseUrl, filename, cancellationToken);
        if (string.IsNullOrWhiteSpace(ttml))
        {
            ttml = await FetchByPlatformIdAsync(baseUrl, candidate, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(ttml))
        {
            return null;
        }

        return new LyricsPayload(
            LyricsFormat.Ttml,
            ttml,
            Id,
            filename,
            new LyricsMetadata(candidate.Title, candidate.Artist, candidate.Album, candidate.Duration));
    }

    private async Task<string?> FetchByFilenameAsync(string baseUrl, string filename, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(filename))
        {
            return null;
        }

        var url = $"{baseUrl}/api/v1/lyrics/get?filename={Uri.EscapeDataString(filename)}";
        return await FetchTtmlAsync(url, cancellationToken);
    }

    private async Task<string?> FetchByPlatformIdAsync(
        string baseUrl,
        LyricsCandidate candidate,
        CancellationToken cancellationToken)
    {
        if (candidate.Extra == null)
        {
            return null;
        }

        var order = new[]
        {
            ("ncmMusicId", "ncmMusicId"),
            ("qqMusicId", "qqMusicId"),
            ("appleMusicId", "appleMusicId"),
            ("spotifyId", "spotifyId"),
            ("isrc", "isrc")
        };

        foreach (var (key, queryName) in order)
        {
            if (!candidate.Extra.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var url = $"{baseUrl}/api/v1/lyrics/get?{queryName}={Uri.EscapeDataString(value)}";
            var ttml = await FetchTtmlAsync(url, cancellationToken);
            if (!string.IsNullOrWhiteSpace(ttml))
            {
                return ttml;
            }
        }

        return null;
    }

    private async Task<string?> FetchTtmlAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await SendWithOptionalRetryAsync(request, cancellationToken);
        if (response == null)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            LyricsHttp.LogHttpFailure(logger, "Amll", response.StatusCode, "get");
            return null;
        }

        var body = await LyricsHttp.ReadBoundedStringAsync(response, LyricsHttp.DefaultMaxBytes, cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            return ParseTtmlResponseBody(body);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "[歌词:Amll] 解析 get 响应封装失败。");
        }

        return null;
    }

    internal static string? ParseTtmlResponseBody(string body)
    {
        // 有些部署直接返回原始 TTML，有些则封装在 JSON 中
        var trimmed = body.TrimStart();
        if (trimmed.StartsWith('<'))
        {
            return body;
        }

        using var document = JsonDocument.Parse(body);
        if (document.RootElement.TryGetProperty("status", out var statusNode) &&
            statusNode.ValueKind == JsonValueKind.Number &&
            statusNode.TryGetInt32(out var status) &&
            status != 200)
        {
            return null;
        }

        var payloadRoot = document.RootElement;
        if (payloadRoot.TryGetProperty("data", out var dataNode))
        {
            if (dataNode.ValueKind == JsonValueKind.String)
            {
                var rawValue = dataNode.GetString();
                return string.IsNullOrWhiteSpace(rawValue) ? null : rawValue;
            }

            if (dataNode.ValueKind == JsonValueKind.Object)
            {
                payloadRoot = dataNode;
            }
        }

        if (payloadRoot.TryGetProperty("format", out var formatNode))
        {
            var format = formatNode.GetString();
            if (!string.IsNullOrWhiteSpace(format) &&
                !format.Equals("ttml", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        foreach (var key in new[] { "lyrics", "ttml", "lyric", "content" })
        {
            if (!payloadRoot.TryGetProperty(key, out var node) || node.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var value = node.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    internal static IReadOnlyList<string> BuildSearchQueries(MediaInfo media)
    {
        var title = LyricsTextNormalizer.NormalizeSearchText(media.Title);
        if (string.IsNullOrWhiteSpace(title))
        {
            return [];
        }

        var titles = new List<string> { title };
        var simplifiedTitle = SimplifySearchTitle(title);
        if (!string.IsNullOrWhiteSpace(simplifiedTitle) &&
            !simplifiedTitle.Equals(title, StringComparison.OrdinalIgnoreCase))
        {
            titles.Add(simplifiedTitle);
        }

        var fullArtist = LyricsTextNormalizer.NormalizeSearchText(media.Artist);
        var artists = new List<string>();
        if (!string.IsNullOrWhiteSpace(fullArtist))
        {
            artists.Add(fullArtist);
        }

        foreach (var artist in LyricsTextNormalizer.SplitArtists(media.Artist))
        {
            if (!artists.Contains(artist, StringComparer.OrdinalIgnoreCase))
            {
                artists.Add(artist);
            }
        }

        var album = LyricsTextNormalizer.NormalizeSearchText(media.AlbumTitle);
        var queries = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddQuery(string queryTitle, string? queryArtist = null, string? queryAlbum = null)
        {
            var parts = new List<string> { $"musicName={Uri.EscapeDataString(queryTitle)}" };
            if (!string.IsNullOrWhiteSpace(queryArtist))
            {
                parts.Add($"artistName={Uri.EscapeDataString(queryArtist)}");
            }

            if (!string.IsNullOrWhiteSpace(queryAlbum))
            {
                parts.Add($"albumName={Uri.EscapeDataString(queryAlbum)}");
            }

            var query = string.Join("&", parts);
            if (seen.Add(query))
            {
                queries.Add(query);
            }
        }

        for (var titleIndex = 0; titleIndex < titles.Count; titleIndex++)
        {
            var queryTitle = titles[titleIndex];
            if (titleIndex == 0 && artists.Count > 0 && !string.IsNullOrWhiteSpace(album))
            {
                AddQuery(queryTitle, artists[0], album);
            }

            foreach (var artist in artists)
            {
                AddQuery(queryTitle, artist);
            }

            AddQuery(queryTitle);
        }

        return queries;
    }

    private static string SimplifySearchTitle(string title)
    {
        var simplified = title;
        foreach (var marker in new[] { " (feat.", " (feat ", " (ft.", " (ft ", " [feat.", " [ft.", "（feat.", "（ft." })
        {
            var index = simplified.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index > 0)
            {
                simplified = simplified[..index];
                break;
            }
        }

        var separator = simplified.LastIndexOf(" - ", StringComparison.Ordinal);
        if (separator > 0)
        {
            var suffix = simplified[(separator + 3)..];
            if (suffix.Contains("remaster", StringComparison.OrdinalIgnoreCase))
            {
                simplified = simplified[..separator];
            }
        }

        return simplified.Trim();
    }

    private async Task<HttpResponseMessage?> SendWithOptionalRetryAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage? response = null;
        try
        {
            response = await HttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if ((int)response.StatusCode != 502)
            {
                return response;
            }

            response.Dispose();
            await Task.Delay(200, cancellationToken);
            using var retry = new HttpRequestMessage(request.Method, request.RequestUri);
            return await HttpClient.SendAsync(
                retry,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            response?.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            response?.Dispose();
            logger?.LogWarning(ex, "[歌词:Amll] 传输失败。");
            return null;
        }
    }

    internal static bool TryGetItems(JsonElement root, out JsonElement.ArrayEnumerator enumerator)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            enumerator = root.EnumerateArray();
            return true;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            enumerator = default;
            return false;
        }

        if (root.TryGetProperty("status", out var statusNode) &&
            statusNode.ValueKind == JsonValueKind.Number &&
            statusNode.TryGetInt32(out var status) &&
            status != 200)
        {
            enumerator = default;
            return false;
        }

        foreach (var key in new[] { "results", "items", "lyrics" })
        {
            if (root.TryGetProperty(key, out var node) && node.ValueKind == JsonValueKind.Array)
            {
                enumerator = node.EnumerateArray();
                return true;
            }
        }

        if (root.TryGetProperty("data", out var data))
        {
            if (data.ValueKind == JsonValueKind.Array)
            {
                enumerator = data.EnumerateArray();
                return true;
            }

            if (data.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in new[] { "items", "results", "lyrics" })
                {
                    if (data.TryGetProperty(key, out var node) && node.ValueKind == JsonValueKind.Array)
                    {
                        enumerator = node.EnumerateArray();
                        return true;
                    }
                }
            }
        }

        enumerator = default;
        return false;
    }

    private static IReadOnlyList<string> ReadStringValues(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var node))
            {
                continue;
            }

            if (node.ValueKind is JsonValueKind.String or JsonValueKind.Number)
            {
                var value = node.ValueKind == JsonValueKind.String ? node.GetString() : node.ToString();
                return string.IsNullOrWhiteSpace(value) ? [] : [value];
            }

            if (node.ValueKind == JsonValueKind.Array)
            {
                return node.EnumerateArray()
                    .Where(value => value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                    .Select(value => value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Cast<string>()
                    .ToArray();
            }
        }

        return [];
    }

    private static string? ReadFirstString(JsonElement element, params string[] propertyNames) =>
        ReadStringValues(element, propertyNames).FirstOrDefault();

    private static void AddFirstValue(
        IDictionary<string, string> target,
        string key,
        IReadOnlyList<string> values)
    {
        var value = values.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(value))
        {
            target[key] = value;
        }
    }
}
