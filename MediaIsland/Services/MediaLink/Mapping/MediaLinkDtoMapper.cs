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
            ChangeKind = changeKind.ToString(),
            SourceApp = media.SourceApp,
            Title = media.Title,
            Artist = media.Artist,
            AlbumTitle = media.AlbumTitle,
            PositionMs = ToMilliseconds(media.Position),
            DurationMs = ToMilliseconds(media.Duration),
            PlaybackState = media.PlaybackInfo.PlaybackState.ToString(),
            PlaybackRate = media.PlaybackInfo.PlaybackRate,
            HasThumbnail = media.Thumbnail is not null || media.ThumbnailSource is not null
        };
    }

    public static MediaLinkLyricsDto? ToLyricsDto(LyricsSearchResult? result)
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
            Source = result.Source.ToString(),
            Document = ToDocumentDto(result.Document)
        };
    }

    public static MediaLinkLyricsDocumentDto ToDocumentDto(LyricsDocument document) =>
        new()
        {
            Format = document.Format.ToString(),
            SyncMode = document.SyncMode.ToString(),
            ProviderItemId = document.ProviderItemId,
            Source = document.Source.ToString(),
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
            LyricsSourceId.External);
        error = null;
        return true;
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

    private static long ToMilliseconds(TimeSpan value) =>
        (long)Math.Round(value.TotalMilliseconds, MidpointRounding.AwayFromZero);
}
