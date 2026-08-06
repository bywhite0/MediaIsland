using System.Security.Cryptography;
using System.Text;
using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Lyrics.Models;
using MediaIsland.Services.Media;
using MediaIsland.Services.MediaLink.Protocol;

namespace MediaIsland.Services.MediaLink.Mapping;

public static class MediaLinkDtoMapper
{
    public static MediaLinkMediaDto? ToMediaDto(MediaInfo? media, MediaInfoChangeKind changeKind)
    {
        if (media is null)
        {
            return null;
        }

        return new MediaLinkMediaDto
        {
            ChangeKind = MapChangeKind(changeKind),
            SourceApp = media.SourceApp,
            Title = media.Title,
            Artist = media.Artist,
            AlbumTitle = media.AlbumTitle,
            PositionMs = ToMilliseconds(media.Position),
            DurationMs = ToMilliseconds(media.Duration),
            PlaybackState = MapPlaybackState(media.PlaybackInfo.PlaybackState),
            PlaybackRate = media.PlaybackInfo.PlaybackRate,
            HasThumbnail = media.Thumbnail is not null || media.ThumbnailSource is not null,
            TrackToken = ComputeTrackToken(media.SourceApp, media.Title, media.Artist, media.AlbumTitle)
        };
    }

    /// <summary>
    /// 映射歌词 DTO。<paramref name="owningMedia"/> 是这份歌词**所属的**媒体，
    /// 其 trackToken 必须与 <c>media.updated.trackToken</c> 同源计算，否则客户端
    /// 按协议规则比对后会丢弃全部歌词。无法确定归属时传 null。
    /// </summary>
    public static MediaLinkLyricsDto? ToLyricsDto(LyricsSearchResult? result, MediaInfo? owningMedia = null)
    {
        if (result is null)
        {
            return null;
        }

        return new MediaLinkLyricsDto
        {
            Id = result.Id,
            Title = result.Title,
            Artist = result.Artist,
            DurationMs = ToMilliseconds(result.Duration),
            Score = result.Score,
            // 转发时报出真实来源而非本机通道，否则每经一跳来源都退化为 External。
            Source = MapLyricsSource(result.OriginSource ?? result.Source),
            TrackToken = owningMedia is null
                ? null
                : ComputeTrackToken(owningMedia.SourceApp, owningMedia.Title, owningMedia.Artist, owningMedia.AlbumTitle),
            Document = ToDocumentDto(result.Document)
        };
    }

    public static MediaLinkLyricsDocumentDto ToDocumentDto(LyricsDocument document) =>
        new()
        {
            Format = MapLyricsFormat(document.Format),
            SyncMode = MapSyncMode(document.SyncMode),
            ProviderItemId = document.ProviderItemId,
            Source = MapLyricsSource(document.Source),
            Metadata = new MediaLinkLyricsMetadataDto
            {
                Title = document.Metadata.Title,
                Artist = document.Metadata.Artist,
                Album = document.Metadata.Album,
                DurationMs = document.Metadata.Duration is { } duration
                    ? ToMilliseconds(duration)
                    : null
            },
            Lines = document.Lines.Select(ToLineDto).ToList()
        };

    private static MediaLinkLyricsLineDto ToLineDto(LyricsLine line) =>
        new()
        {
            StartMs = ToMilliseconds(line.StartTime),
            EndMs = ToMilliseconds(line.EndTime),
            Text = line.Text,
            Translation = line.Translation,
            Romanization = line.Romanization,
            IsBackground = line.IsBackground,
            IsDuet = line.IsDuet,
            Words = line.Words.Select(word => new MediaLinkLyricsWordDto
            {
                StartMs = ToMilliseconds(word.StartTime),
                EndMs = ToMilliseconds(word.EndTime),
                Text = word.Text
            }).ToList(),
            RubySpans = (line.RubySpans ?? [])
                .Select(ruby => new MediaLinkLyricsRubySpanDto
                {
                    BaseStart = ruby.BaseStart,
                    BaseLength = ruby.BaseLength,
                    Reading = ruby.Reading
                }).ToList()
        };

    /// <summary>
    /// 把收到的 <c>media.updated</c> 转成可转发的 <c>media.inject</c> 载荷。
    ///
    /// <paramref name="elapsedSinceReceiveMs"/> 是本地收帧后经过的时间。它与
    /// 上游的 <c>serverTimeMs - positionCapturedAtMs</c> 相加得到 positionAgeMs：
    /// 两段各自在同一时钟内求得，故跨机转发无需两端对时。
    /// </summary>
    public static MediaLinkMediaInjectPayload ToInjectPayload(
        MediaLinkMediaDto dto,
        long elapsedSinceReceiveMs)
    {
        ArgumentNullException.ThrowIfNull(dto);

        var captureLagMs = dto.ServerTimeMs > 0 && dto.PositionCapturedAtMs > 0
            ? dto.ServerTimeMs - dto.PositionCapturedAtMs
            : 0;

        // 时钟回拨或字段缺失都可能让差值为负；负的"年龄"没有意义，钳到 0。
        var ageMs = Math.Max(0, captureLagMs + Math.Max(0, elapsedSinceReceiveMs));

        return new MediaLinkMediaInjectPayload
        {
            SourceApp = dto.SourceApp,
            Title = dto.Title,
            Artist = dto.Artist,
            AlbumTitle = dto.AlbumTitle,
            PositionMs = dto.PositionMs,
            DurationMs = dto.DurationMs,
            PlaybackState = dto.PlaybackState,
            PlaybackRate = dto.PlaybackRate,
            PositionAgeMs = ageMs
        };
    }

    public static bool TryMapInjectedLyrics(
        MediaLinkLyricsDto payload,
        out LyricsSearchResult? result,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var documentDto = payload.Document ?? new MediaLinkLyricsDocumentDto { Lines = [] };
        var lines = MapLines(documentDto.Lines);
        var metadata = documentDto.Metadata ?? new MediaLinkLyricsMetadataDto();
        var duration = ResolveDuration(payload.DurationMs, metadata.DurationMs);
        var id = string.IsNullOrWhiteSpace(payload.Id)
            ? Guid.NewGuid().ToString("N")
            : payload.Id.Trim();
        var title = FirstNonEmpty(payload.Title, metadata.Title) ?? string.Empty;
        var artist = FirstNonEmpty(payload.Artist, metadata.Artist) ?? string.Empty;
        var album = string.IsNullOrWhiteSpace(metadata.Album) ? null : metadata.Album.Trim();
        var providerItemId = string.IsNullOrWhiteSpace(documentDto.ProviderItemId)
            ? id
            : documentDto.ProviderItemId.Trim();
        var format = ParseOrDefault(documentDto.Format, LyricsFormat.Unknown);
        var syncMode = InferSyncMode(documentDto.SyncMode, lines);

        // 上游标注的真实来源，仅用于显示。通道必须保持 External：
        // 组件按 Source == External 决定是否直接应用注入歌词，改掉会让歌词不再显示。
        var originSource = ParseLyricsSource(FirstNonEmpty(documentDto.Source, payload.Source));

        var document = new LyricsDocument(
            new LyricsMetadata(title, artist, album, duration > TimeSpan.Zero ? duration : null),
            lines,
            syncMode,
            LyricsSourceId.External,
            providerItemId,
            format);

        result = new LyricsSearchResult(
            document,
            id,
            title,
            artist,
            duration,
            payload.Score,
            LyricsSourceId.External,
            originSource);
        error = null;
        return true;
    }

    /// <summary>
    /// 解析 wire 上的来源名。未知值（含上游新增的来源）返回 null，按"来源不详"处理，
    /// 与协议要求的"必须容忍未知枚举值"一致。
    /// </summary>
    internal static LyricsSourceId? ParseLyricsSource(string? wireValue)
    {
        if (string.IsNullOrWhiteSpace(wireValue))
        {
            return null;
        }

        return Enum.TryParse<LyricsSourceId>(wireValue.Trim(), ignoreCase: true, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// 根据曲目标识（SourceApp + Title + Artist + AlbumTitle）计算稳定的 trackToken。
    /// 同一曲目多次调用结果一致；不同曲目碰撞概率极低。
    /// 分隔符用 U+001F，避免字段内容拼接产生歧义。
    /// </summary>
    internal static string ComputeTrackToken(string sourceApp, string? title, string? artist, string? albumTitle = null)
    {
        var key = string.Join('\x1F', sourceApp, title ?? string.Empty, artist ?? string.Empty, albumTitle ?? string.Empty);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    private static IReadOnlyList<LyricsLine> MapLines(IEnumerable<MediaLinkLyricsLineDto>? lineDtos)
    {
        if (lineDtos is null)
        {
            return [];
        }

        return lineDtos.Select(line =>
        {
            var words = (line.Words ?? [])
                .Select(word => new LyricsWord(
                    TimeSpan.FromMilliseconds(word.StartMs),
                    TimeSpan.FromMilliseconds(word.EndMs),
                    word.Text ?? string.Empty))
                .ToList();
            var rubies = (line.RubySpans ?? [])
                .Select(ruby => new LyricsRubySpan(ruby.BaseStart, ruby.BaseLength, ruby.Reading ?? string.Empty))
                .ToList();

            return new LyricsLine(
                TimeSpan.FromMilliseconds(line.StartMs),
                TimeSpan.FromMilliseconds(line.EndMs),
                line.Text ?? string.Empty,
                words,
                line.Translation,
                line.Romanization,
                line.IsBackground,
                line.IsDuet,
                rubies.Count == 0 ? null : rubies);
        }).ToList();
    }

    private static TimeSpan ResolveDuration(long payloadDurationMs, long? metadataDurationMs)
    {
        if (payloadDurationMs > 0)
        {
            return TimeSpan.FromMilliseconds(payloadDurationMs);
        }

        if (metadataDurationMs is > 0)
        {
            return TimeSpan.FromMilliseconds(metadataDurationMs.Value);
        }

        return TimeSpan.Zero;
    }

    private static LyricsSyncMode InferSyncMode(string? raw, IReadOnlyList<LyricsLine> lines)
    {
        if (!string.IsNullOrWhiteSpace(raw) &&
            Enum.TryParse<LyricsSyncMode>(raw, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        if (lines.Any(line => line.Words.Count > 0))
        {
            return LyricsSyncMode.Word;
        }

        if (lines.Count > 0)
        {
            return LyricsSyncMode.Line;
        }

        return LyricsSyncMode.Unsynced;
    }

    private static TEnum ParseOrDefault<TEnum>(string? raw, TEnum fallback)
        where TEnum : struct, Enum
    {
        if (!string.IsNullOrWhiteSpace(raw) &&
            Enum.TryParse<TEnum>(raw, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return fallback;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    /// <summary>安全映射播放状态：未知值输出 "Unknown"，不抛异常。</summary>
    internal static string MapPlaybackState(MediaPlaybackState state) => state switch
    {
        MediaPlaybackState.Unknown => nameof(MediaPlaybackState.Unknown),
        MediaPlaybackState.Closed => nameof(MediaPlaybackState.Closed),
        MediaPlaybackState.Opened => nameof(MediaPlaybackState.Opened),
        MediaPlaybackState.Changing => nameof(MediaPlaybackState.Changing),
        MediaPlaybackState.Stopped => nameof(MediaPlaybackState.Stopped),
        MediaPlaybackState.Playing => nameof(MediaPlaybackState.Playing),
        MediaPlaybackState.Paused => nameof(MediaPlaybackState.Paused),
        _ => "Unknown"
    };

    /// <summary>安全映射歌词来源：wire 值与内部枚举解耦，未知值输出 "Unknown"。</summary>
    internal static string MapLyricsSource(LyricsSourceId source) => source switch
    {
        LyricsSourceId.Netease => nameof(LyricsSourceId.Netease),
        LyricsSourceId.QqMusic => nameof(LyricsSourceId.QqMusic),
        LyricsSourceId.Kugou => nameof(LyricsSourceId.Kugou),
        LyricsSourceId.AmllTtml => nameof(LyricsSourceId.AmllTtml),
        LyricsSourceId.SPlayerNext => nameof(LyricsSourceId.SPlayerNext),
        LyricsSourceId.External => nameof(LyricsSourceId.External),
        LyricsSourceId.LocalFile => nameof(LyricsSourceId.LocalFile),
        _ => "Unknown"
    };

    /// <summary>安全映射歌词格式：未知值输出 "Unknown"。</summary>
    internal static string MapLyricsFormat(LyricsFormat format) => format switch
    {
        LyricsFormat.Unknown => nameof(LyricsFormat.Unknown),
        LyricsFormat.Lrc => nameof(LyricsFormat.Lrc),
        LyricsFormat.Qrc => nameof(LyricsFormat.Qrc),
        LyricsFormat.Krc => nameof(LyricsFormat.Krc),
        LyricsFormat.Ttml => nameof(LyricsFormat.Ttml),
        _ => "Unknown"
    };

    /// <summary>安全映射歌词同步模式：未知值输出 "Unknown"。</summary>
    internal static string MapSyncMode(LyricsSyncMode syncMode) => syncMode switch
    {
        LyricsSyncMode.Unsynced => nameof(LyricsSyncMode.Unsynced),
        LyricsSyncMode.Line => nameof(LyricsSyncMode.Line),
        LyricsSyncMode.Word => nameof(LyricsSyncMode.Word),
        _ => "Unknown"
    };

    /// <summary>安全映射变更类型：未知值输出 "Unknown"，不抛异常。</summary>
    internal static string MapChangeKind(MediaInfoChangeKind kind) => kind switch
    {
        MediaInfoChangeKind.CurrentSession => nameof(MediaInfoChangeKind.CurrentSession),
        MediaInfoChangeKind.MediaProperties => nameof(MediaInfoChangeKind.MediaProperties),
        MediaInfoChangeKind.Playback => nameof(MediaInfoChangeKind.Playback),
        MediaInfoChangeKind.Timeline => nameof(MediaInfoChangeKind.Timeline),
        _ => "Unknown"
    };
    private static long ToMilliseconds(TimeSpan value) =>
        (long)Math.Round(value.TotalMilliseconds, MidpointRounding.AwayFromZero);
}