using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MediaIsland.Services.Lyrics.Models;
using MediaIsland.Services.Lyrics.Parsers;
using MediaIsland.Services.Media;
using Microsoft.Extensions.Logging;

namespace MediaIsland.Services.Lyrics.Providers;

public sealed class QqMusicLyricsProvider(ILogger<QqMusicLyricsProvider>? logger = null) : ILyricsProvider
{
    private static readonly HttpClient HttpClient = LyricsHttp.CreateClient("qqmusic");
    private static readonly Regex JsonpRegex = new(@"^[^{]*(\{.*\})[^}]*$", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex MalformedEmptyElementRegex = new(
        "<[A-Za-z_][\\w:.-]*=\"[^\"]*\"\\s*/>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public LyricsSourceId Id => LyricsSourceId.QqMusic;

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
            var page = await SearchSongsAsync(query, cancellationToken);
            if (page.Count == 0)
            {
                continue;
            }

            foreach (var item in page)
            {
                var score = LyricsCandidateScorer.Score(
                    media,
                    item.Title,
                    item.Artist,
                    item.Album,
                    item.Duration);
                if (score < LyricsCandidateScorer.MinimumScore(media))
                {
                    continue;
                }

                candidates.Add(new LyricsCandidate(
                    Id,
                    item.Id,
                    item.Title,
                    item.Artist,
                    item.Album,
                    item.Duration,
                    score,
                    SupportsWordSync: true,
                    Extra: new Dictionary<string, string>
                    {
                        ["mid"] = item.Mid,
                        ["id"] = item.Id
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

        var id = candidate.Extra != null && candidate.Extra.TryGetValue("id", out var explicitId)
            ? explicitId
            : candidate.ProviderItemId;
        var mid = candidate.Extra != null && candidate.Extra.TryGetValue("mid", out var explicitMid)
            ? explicitMid
            : string.Empty;

        if (settings.PreferWordSync(Id))
        {
            var qrc = await TryFetchQrcAsync(id, cancellationToken);
            if (qrc != null)
            {
                return qrc with
                {
                    Metadata = new LyricsMetadata(candidate.Title, candidate.Artist, candidate.Album, candidate.Duration)
                };
            }
        }

        var lrc = await TryFetchLrcAsync(mid, cancellationToken);
        if (lrc == null)
        {
            return null;
        }

        return lrc with
        {
            Metadata = new LyricsMetadata(candidate.Title, candidate.Artist, candidate.Album, candidate.Duration),
            ProviderItemId = candidate.ProviderItemId
        };
    }

    private async Task<IReadOnlyList<QqSong>> SearchSongsAsync(string query, CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object>
        {
            ["music.search.SearchCgiService"] = new
            {
                method = "DoSearchForQQMusicDesktop",
                module = "music.search.SearchCgiService",
                param = new
                {
                    num_per_page = 10,
                    page_num = 1,
                    query,
                    search_type = 0
                }
            }
        };

        var json = JsonSerializer.Serialize(payload);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://u.y.qq.com/cgi-bin/musicu.fcg")
        {
            Content = content
        };
        request.Headers.Referrer = new Uri("https://y.qq.com/");

        using var response = await HttpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            LyricsHttp.LogHttpFailure(logger, "QqMusic", response.StatusCode);
            return [];
        }

        using var document = await LyricsHttp.ReadJsonDocumentAsync(response, LyricsHttp.DefaultMaxBytes, cancellationToken);
        if (document == null)
        {
            return [];
        }

        if ((!document.RootElement.TryGetProperty("music.search.SearchCgiService", out var req) &&
             !document.RootElement.TryGetProperty("req_1", out req)) ||
            !req.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("body", out var body) ||
            !body.TryGetProperty("song", out var song) ||
            !song.TryGetProperty("list", out var list) ||
            list.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<QqSong>();
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = item.TryGetProperty("id", out var idNode) ? idNode.ToString() : string.Empty;
            var mid = item.TryGetProperty("mid", out var midNode) ? midNode.GetString() ?? string.Empty : string.Empty;
            var title = item.TryGetProperty("title", out var titleNode)
                ? titleNode.GetString() ?? string.Empty
                : item.TryGetProperty("name", out var nameNode) ? nameNode.GetString() ?? string.Empty : string.Empty;
            var album = item.TryGetProperty("album", out var albumNode) && albumNode.ValueKind == JsonValueKind.Object &&
                        albumNode.TryGetProperty("title", out var albumTitle)
                ? albumTitle.GetString() ?? string.Empty
                : string.Empty;
            var artist = string.Empty;
            if (item.TryGetProperty("singer", out var singers) && singers.ValueKind == JsonValueKind.Array)
            {
                artist = string.Join(" ", singers.EnumerateArray()
                    .Select(s => s.TryGetProperty("name", out var n) ? n.GetString() : null)
                    .Where(v => !string.IsNullOrWhiteSpace(v)));
            }

            var duration = TimeSpan.Zero;
            if (item.TryGetProperty("interval", out var interval) && interval.TryGetInt32(out var seconds))
            {
                duration = TimeSpan.FromSeconds(seconds);
            }

            if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(mid))
            {
                continue;
            }

            results.Add(new QqSong(id, mid, title, artist, album, duration));
        }

        return results;
    }

    private async Task<LyricsPayload?> TryFetchQrcAsync(string id, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["version"] = "15",
                ["miniversion"] = "82",
                ["lrctype"] = "4",
                ["musicid"] = id
            });
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                "https://c.y.qq.com/qqmusic/fcgi-bin/lyric_download.fcg")
            {
                Content = content
            };
            request.Headers.Referrer = new Uri("https://y.qq.com/");
            request.Headers.ConnectionClose = true;
            using var response = await HttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                LyricsHttp.LogHttpFailure(logger, "QqMusic", response.StatusCode, "qrc");
                return null;
            }

            var body = await LyricsHttp.ReadBoundedStringAsync(
                response,
                LyricsHttp.DefaultMaxBytes,
                cancellationToken);
            var encrypted = ParseQrcResponseBody(body);
            if (string.IsNullOrWhiteSpace(encrypted.Original))
            {
                return null;
            }

            var decrypted = DecodeQrcValue(encrypted.Original);
            var translation = DecodeQrcValue(encrypted.Translation);
            if (string.IsNullOrWhiteSpace(decrypted))
            {
                return null;
            }

            return new LyricsPayload(
                LyricsFormat.Qrc,
                decrypted,
                Id,
                id,
                new LyricsMetadata(null, null, null, null),
                translation);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger?.LogWarning("[Lyrics:QqMusic] QRC fetch timed out for id={Id}", id);
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "[Lyrics:QqMusic] QRC fetch failed for id={Id}", id);
            return null;
        }
    }

    internal static (string? Original, string? Translation) ParseQrcResponseBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return (null, null);
        }

        var xml = body.Trim();
        if (xml.StartsWith("<!--", StringComparison.Ordinal))
        {
            xml = xml[4..];
        }

        if (xml.EndsWith("-->", StringComparison.Ordinal))
        {
            xml = xml[..^3];
        }

        xml = MalformedEmptyElementRegex.Replace(xml, string.Empty);
        var document = XDocument.Parse(xml, LoadOptions.None);
        return (
            ReadElementValue(document, "content"),
            ReadElementValue(document, "contentts"));
    }

    private static string? DecodeQrcValue(string? encrypted)
    {
        if (string.IsNullOrWhiteSpace(encrypted))
        {
            return null;
        }

        var decrypted = ManagedLyricsPayloadParser.DecryptQrc(encrypted);
        if (string.IsNullOrWhiteSpace(decrypted))
        {
            return null;
        }

        if (!decrypted.Contains('<', StringComparison.Ordinal))
        {
            return decrypted;
        }

        try
        {
            var document = XDocument.Parse(decrypted, LoadOptions.None);
            var lyricNode = document.Descendants()
                .FirstOrDefault(element => element.Name.LocalName.Equals("Lyric_1", StringComparison.OrdinalIgnoreCase));
            var content = lyricNode?.Attributes()
                .FirstOrDefault(attribute => attribute.Name.LocalName.Equals("LyricContent", StringComparison.OrdinalIgnoreCase))
                ?.Value;
            return string.IsNullOrWhiteSpace(content) ? decrypted : content;
        }
        catch
        {
            return decrypted;
        }
    }

    private static string? ReadElementValue(XContainer document, string localName)
    {
        var value = document.Descendants()
            .FirstOrDefault(element => element.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))
            ?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private async Task<LyricsPayload?> TryFetchLrcAsync(string mid, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(mid))
        {
            return null;
        }

        var url =
            $"https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg?songmid={Uri.EscapeDataString(mid)}&g_tk=5381&format=json&nobase64=1";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Referrer = new Uri("https://y.qq.com/");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await HttpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            LyricsHttp.LogHttpFailure(logger, "QqMusic", response.StatusCode, "lrc");
            return null;
        }

        var body = await LyricsHttp.ReadBoundedStringAsync(response, LyricsHttp.DefaultMaxBytes, cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        var match = JsonpRegex.Match(body);
        var jsonText = match.Success ? match.Groups[1].Value : body;
        using var document = JsonDocument.Parse(jsonText);
        if (!document.RootElement.TryGetProperty("lyric", out var lyricNode))
        {
            return null;
        }

        var lyric = lyricNode.GetString();
        if (string.IsNullOrWhiteSpace(lyric))
        {
            return null;
        }

        string? translation = null;
        if (document.RootElement.TryGetProperty("trans", out var transNode))
        {
            translation = transNode.GetString();
        }

        return new LyricsPayload(
            LyricsFormat.Lrc,
            lyric,
            Id,
            mid,
            new LyricsMetadata(null, null, null, null),
            translation);
    }

    private sealed record QqSong(string Id, string Mid, string Title, string Artist, string Album, TimeSpan Duration);
}
