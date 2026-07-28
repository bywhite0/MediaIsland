using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Lyrics.Models;
using MediaIsland.Services.Media;
using MediaIsland.Services.Realtime.Protocol;

namespace MediaIsland.Services.Realtime.Mapping;

public static class RealtimeDtoMapper
{
    public static RealtimeMediaDto? ToMediaDto(MediaInfo? media, MediaInfoChangeKind changeKind)
    {
        if (media is null)
        {
            return null;
        }

        return new RealtimeMediaDto
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

    public static RealtimeLyricsDto? ToLyricsDto(LyricsSearchResult? result)
    {
        if (result is null)
        {
            return null;
        }

        return new RealtimeLyricsDto
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

    public static RealtimeLyricsDocumentDto ToDocumentDto(LyricsDocument document) =>
        new()
        {
            Format = document.Format.ToString(),
            SyncMode = document.SyncMode.ToString(),
            ProviderItemId = document.ProviderItemId,
            Source = document.Source.ToString(),
            Metadata = new RealtimeLyricsMetadataDto
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

    private static RealtimeLyricsLineDto ToLineDto(LyricsLine line) =>
        new()
        {
            StartMs = ToMilliseconds(line.StartTime),
            EndMs = ToMilliseconds(line.EndTime),
            Text = line.Text,
            Translation = line.Translation,
            Romanization = line.Romanization,
            IsBackground = line.IsBackground,
            IsDuet = line.IsDuet,
            Words = line.Words.Select(word => new RealtimeLyricsWordDto
            {
                StartMs = ToMilliseconds(word.StartTime),
                EndMs = ToMilliseconds(word.EndTime),
                Text = word.Text
            }).ToList(),
            RubySpans = (line.RubySpans ?? [])
                .Select(ruby => new RealtimeLyricsRubySpanDto
                {
                    BaseStart = ruby.BaseStart,
                    BaseLength = ruby.BaseLength,
                    Reading = ruby.Reading
                }).ToList()
        };

    private static long ToMilliseconds(TimeSpan value) =>
        (long)Math.Round(value.TotalMilliseconds, MidpointRounding.AwayFromZero);
}
