using System.Text.Json;
using MediaIsland.Helpers;
using MediaIsland.Services.Lyrics.Models;
using MediaIsland.Services.Media;
using Microsoft.Extensions.Logging;

namespace MediaIsland.Services.Lyrics.Providers;

/// <summary>
/// 从 SPlayer-Next 外部 API 读取当前曲目已解析歌词。
/// </summary>
public sealed class SPlayerNextLyricsClient(ILogger<SPlayerNextLyricsClient>? logger = null)
{
    private static readonly HttpClient HttpClient = LyricsHttp.CreateClient("splayer-next");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<LyricsSearchResult?> TryFetchAsync(
        MediaInfo media,
        LyricsSourceSettings settings,
        CancellationToken cancellationToken)
    {
        if (!SPlayerNextMediaSource.Matches(media.SourceApp) || string.IsNullOrWhiteSpace(media.Title))
        {
            return null;
        }

        var baseUrl = LyricsSourceSettings.NormalizeSPlayerNextBaseUrl(settings.SPlayerNextApiBaseUrl);
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return null;
        }

        try
        {
            var lyricsUrl = $"{baseUrl}/api/lyrics";
            using var lyricsResponse = await HttpClient.GetAsync(lyricsUrl, cancellationToken).ConfigureAwait(false);
            if (!lyricsResponse.IsSuccessStatusCode)
            {
                LyricsHttp.LogHttpFailure(logger, "SPlayerNext", lyricsResponse.StatusCode, "lyrics");
                return null;
            }

            await using var lyricsStream = await lyricsResponse.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            var snapshot = await JsonSerializer.DeserializeAsync<SPlayerLyricsSnapshot>(
                    lyricsStream,
                    JsonOptions,
                    cancellationToken)
                .ConfigureAwait(false);
            if (snapshot?.Lyric is not { Count: > 0 })
            {
                logger?.LogInformation("[歌词:SPlayerNext] 外部 API 当前无歌词。");
                return null;
            }

            // 用 now-playing 校验曲目，避免 SMTC 与 API 短暂不同步时套用错歌歌词。
            var nowPlaying = await TryReadNowPlayingAsync(baseUrl, cancellationToken).ConfigureAwait(false);
            if (nowPlaying?.Track != null && !IsSameTrack(media, nowPlaying.Track))
            {
                logger?.LogInformation(
                    "[歌词:SPlayerNext] 跳过：API 曲目与当前媒体不一致（{ApiTitle} vs {MediaTitle}）。",
                    nowPlaying.Track.Title,
                    media.Title);
                return null;
            }

            var track = nowPlaying?.Track;
            var title = FirstNonEmpty(track?.Title, media.Title) ?? string.Empty;
            var artist = FirstNonEmpty(FormatArtists(track?.Artists), media.Artist) ?? string.Empty;
            var album = FirstNonEmpty(track?.Album?.Name, media.AlbumTitle) ?? string.Empty;
            var duration = track?.Duration is > 0
                ? TimeSpan.FromMilliseconds(track.Duration.Value)
                : media.Duration;
            var trackId = FirstNonEmpty(snapshot.TrackId, track?.Id) ?? title;
            var offsetMs = snapshot.LyricOffsetMs;
            // 保留 API 解析出的逐字信息；组件侧再按全局开关折叠显示。
            const bool preferWordSync = true;

            var lines = ConvertLines(snapshot.Lyric, offsetMs);
            if (lines.Count == 0)
            {
                return null;
            }

            var format = MapFormat(snapshot.Source?.Format);
            var metadata = new LyricsMetadata(title, artist, album, duration > TimeSpan.Zero ? duration : null);
            var document = LyricsDocumentNormalizer.Create(
                lines,
                metadata,
                LyricsSourceId.SPlayerNext,
                trackId,
                format,
                preferWordSync: preferWordSync);

            if (document.Lines.Count == 0)
            {
                return null;
            }

            logger?.LogInformation(
                "[歌词] 已选择 SPlayerNext/{Format}，同步方式={Sync}，ID={Id}，行数={Lines}，标题={Title}",
                document.Format,
                document.SyncMode,
                document.ProviderItemId,
                document.Lines.Count,
                title);

            return new LyricsSearchResult(
                document,
                document.ProviderItemId,
                title,
                artist,
                duration,
                Score: 1000,
                LyricsSourceId.SPlayerNext);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "[歌词:SPlayerNext] 获取外部 API 歌词失败。");
            return null;
        }
    }

    public async Task<bool> TestConnectionAsync(string? baseUrl, CancellationToken cancellationToken = default)
    {
        var normalized = LyricsSourceSettings.NormalizeSPlayerNextBaseUrl(baseUrl);
        using var response = await HttpClient.GetAsync($"{normalized}/api/info", cancellationToken)
            .ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    private async Task<SPlayerNowPlayingSnapshot?> TryReadNowPlayingAsync(
        string baseUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await HttpClient.GetAsync($"{baseUrl}/api/now-playing", cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<SPlayerNowPlayingSnapshot>(
                    stream,
                    JsonOptions,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    internal static bool IsSameTrack(MediaInfo media, SPlayerTrack track)
    {
        var mediaTitle = LyricsTextNormalizer.NormalizeComparableText(media.Title);
        var apiTitle = LyricsTextNormalizer.NormalizeComparableText(track.Title);
        if (string.IsNullOrWhiteSpace(mediaTitle) || string.IsNullOrWhiteSpace(apiTitle))
        {
            return false;
        }

        if (!mediaTitle.Equals(apiTitle, StringComparison.OrdinalIgnoreCase) &&
            !mediaTitle.Contains(apiTitle, StringComparison.OrdinalIgnoreCase) &&
            !apiTitle.Contains(mediaTitle, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var mediaArtist = LyricsTextNormalizer.NormalizeComparableText(media.Artist);
        var apiArtist = LyricsTextNormalizer.NormalizeComparableText(FormatArtists(track.Artists));
        if (string.IsNullOrWhiteSpace(mediaArtist) || string.IsNullOrWhiteSpace(apiArtist))
        {
            return true;
        }

        var mediaArtists = LyricsTextNormalizer.SplitArtists(media.Artist);
        var apiArtists = LyricsTextNormalizer.SplitArtists(FormatArtists(track.Artists));
        if (mediaArtists.Count == 0 || apiArtists.Count == 0)
        {
            return mediaArtist.Contains(apiArtist, StringComparison.OrdinalIgnoreCase) ||
                   apiArtist.Contains(mediaArtist, StringComparison.OrdinalIgnoreCase);
        }

        return mediaArtists.Any(left =>
            apiArtists.Any(right =>
                left.Equals(right, StringComparison.OrdinalIgnoreCase) ||
                left.Contains(right, StringComparison.OrdinalIgnoreCase) ||
                right.Contains(left, StringComparison.OrdinalIgnoreCase)));
    }

    internal static IReadOnlyList<LyricsLine> ConvertLines(
        IReadOnlyList<SPlayerLyricLine> sourceLines,
        int lyricOffsetMs)
    {
        var lines = new List<LyricsLine>(sourceLines.Count);
        foreach (var line in sourceLines)
        {
            var words = (line.Words ?? [])
                .Select(word => new LyricsWord(
                    ApplyOffset(FromMilliseconds(word.StartTime), lyricOffsetMs),
                    ApplyOffset(FromMilliseconds(word.EndTime), lyricOffsetMs),
                    word.Word ?? string.Empty))
                .Where(word => !string.IsNullOrEmpty(word.Text) || word.EndTime > word.StartTime)
                .ToArray();

            var text = words.Length > 0
                ? string.Concat(words.Select(word => word.Text))
                : string.Empty;
            if (string.IsNullOrWhiteSpace(text) &&
                string.IsNullOrWhiteSpace(line.TranslatedLyric) &&
                string.IsNullOrWhiteSpace(line.RomanLyric))
            {
                continue;
            }

            var start = ApplyOffset(FromMilliseconds(line.StartTime), lyricOffsetMs);
            var end = ApplyOffset(FromMilliseconds(line.EndTime), lyricOffsetMs);
            if (words.Length > 0)
            {
                if (start <= TimeSpan.Zero)
                {
                    start = words[0].StartTime;
                }

                if (end <= start)
                {
                    end = words[^1].EndTime;
                }
            }

            lines.Add(new LyricsLine(
                start,
                end,
                text,
                words,
                string.IsNullOrWhiteSpace(line.TranslatedLyric) ? null : line.TranslatedLyric.Trim(),
                string.IsNullOrWhiteSpace(line.RomanLyric) ? null : line.RomanLyric.Trim(),
                line.IsBG,
                line.IsDuet));
        }

        return lines;
    }

    internal static LyricsFormat MapFormat(string? format) =>
        format?.Trim().ToLowerInvariant() switch
        {
            "ttml" => LyricsFormat.Ttml,
            "qrc" => LyricsFormat.Qrc,
            "krc" => LyricsFormat.Krc,
            "lrc" or "yrc" or "lys" or "srt" or "ass" => LyricsFormat.Lrc,
            _ => LyricsFormat.Unknown
        };

    private static TimeSpan FromMilliseconds(double value) =>
        TimeSpan.FromMilliseconds(Math.Max(0, value));

    /// <summary>
    /// SPlayer 的 lyricOffsetMs 与 MediaIsland 全局偏移同语义（正值=歌词提前），
    /// 等价于播放位置加上偏移；这里把偏移烘焙进时间戳。
    /// </summary>
    private static TimeSpan ApplyOffset(TimeSpan time, int lyricOffsetMs)
    {
        if (lyricOffsetMs == 0)
        {
            return time;
        }

        var shifted = time - TimeSpan.FromMilliseconds(lyricOffsetMs);
        return shifted < TimeSpan.Zero ? TimeSpan.Zero : shifted;
    }

    private static string? FormatArtists(IReadOnlyList<SPlayerArtist>? artists)
    {
        if (artists is not { Count: > 0 })
        {
            return null;
        }

        var names = artists
            .Select(artist => artist.Name?.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToArray();
        return names.Length == 0 ? null : string.Join(" / ", names);
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    internal sealed class SPlayerLyricsSnapshot
    {
        public string? TrackId { get; set; }
        public List<SPlayerLyricLine>? Lyric { get; set; }
        public SPlayerLyricSource? Source { get; set; }
        public int LyricOffsetMs { get; set; }
    }

    internal sealed class SPlayerLyricSource
    {
        public string? Source { get; set; }
        public string? Format { get; set; }
        public string? Platform { get; set; }
    }

    internal sealed class SPlayerLyricLine
    {
        public List<SPlayerLyricWord>? Words { get; set; }
        public string? TranslatedLyric { get; set; }
        public string? RomanLyric { get; set; }
        public double StartTime { get; set; }
        public double EndTime { get; set; }
        public bool IsBG { get; set; }
        public bool IsDuet { get; set; }
    }

    internal sealed class SPlayerLyricWord
    {
        public string? Word { get; set; }
        public double StartTime { get; set; }
        public double EndTime { get; set; }
    }

    internal sealed class SPlayerNowPlayingSnapshot
    {
        public SPlayerTrack? Track { get; set; }
        public bool LyricAvailable { get; set; }
        public int LyricLineCount { get; set; }
    }

    internal sealed class SPlayerTrack
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public List<SPlayerArtist>? Artists { get; set; }
        public SPlayerAlbum? Album { get; set; }
        public double? Duration { get; set; }
    }

    internal sealed class SPlayerArtist
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
    }

    internal sealed class SPlayerAlbum
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
    }
}
