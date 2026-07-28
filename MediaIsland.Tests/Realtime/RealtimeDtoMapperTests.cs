using System.Text.Json;
using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Lyrics.Models;
using MediaIsland.Services.Media;
using MediaIsland.Services.Realtime.Mapping;
using MediaIsland.Services.Realtime.Protocol;
using Xunit;

namespace MediaIsland.Tests.Realtime;

public class RealtimeDtoMapperTests
{
    [Fact]
    public void ToMediaDto_ReturnsNull_WhenMediaNull()
    {
        Assert.Null(RealtimeDtoMapper.ToMediaDto(null, MediaInfoChangeKind.Timeline));
    }

    [Fact]
    public void ToMediaDto_MapsFields_WithoutBitmapProperties_AndHasThumbnail()
    {
        var media = new MediaInfo(
            "Spotify.exe",
            "Song",
            "Artist",
            "Album",
            TimeSpan.FromMilliseconds(12_345),
            TimeSpan.FromMinutes(4),
            new MediaPlaybackInfo(MediaPlaybackState.Playing, 1.0),
            Thumbnail: null,
            ThumbnailSource: null);

        var dto = RealtimeDtoMapper.ToMediaDto(media, MediaInfoChangeKind.MediaProperties);
        Assert.NotNull(dto);
        Assert.Equal(nameof(MediaInfoChangeKind.MediaProperties), dto.ChangeKind);
        Assert.Equal("Spotify.exe", dto.SourceApp);
        Assert.Equal("Song", dto.Title);
        Assert.Equal("Artist", dto.Artist);
        Assert.Equal("Album", dto.AlbumTitle);
        Assert.Equal(12_345, dto.PositionMs);
        Assert.Equal(240_000, dto.DurationMs);
        Assert.Equal(nameof(MediaPlaybackState.Playing), dto.PlaybackState);
        Assert.Equal(1.0, dto.PlaybackRate);
        Assert.False(dto.HasThumbnail);

        var json = RealtimeMessageSerializer.SerializePayload(dto);
        Assert.DoesNotContain("\"thumbnail\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"bitmap\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"hasThumbnail\":false", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ToMediaDto_HasThumbnailTrue_WhenThumbnailSourcePresent()
    {
        var media = new MediaInfo(
            "app",
            "t",
            "a",
            null,
            TimeSpan.Zero,
            TimeSpan.Zero,
            new MediaPlaybackInfo(MediaPlaybackState.Paused),
            Thumbnail: null,
            ThumbnailSource: new MediaThumbnail((_, _) => Task.FromResult<Avalonia.Media.Imaging.Bitmap?>(null)));

        var dto = RealtimeDtoMapper.ToMediaDto(media, MediaInfoChangeKind.CurrentSession);
        Assert.NotNull(dto);
        Assert.True(dto.HasThumbnail);
    }

    [Fact]
    public void ToLyricsDto_ReturnsNull_WhenResultNull()
    {
        Assert.Null(RealtimeDtoMapper.ToLyricsDto(null));
    }

    [Fact]
    public void ToLyricsDto_RoundTripsFullDocument_WithWordsAndRuby()
    {
        var document = new LyricsDocument(
            new LyricsMetadata("Title", "Artist", "Album", TimeSpan.FromSeconds(90)),
            [
                new LyricsLine(
                    TimeSpan.FromMilliseconds(100),
                    TimeSpan.FromMilliseconds(1100),
                    "漢字",
                    [
                        new LyricsWord(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(400), "漢"),
                        new LyricsWord(TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(1100), "字")
                    ],
                    Translation: "hanzi",
                    Romanization: "kanji",
                    IsBackground: false,
                    IsDuet: true,
                    RubySpans:
                    [
                        new LyricsRubySpan(0, 1, "かん"),
                        new LyricsRubySpan(1, 1, "じ")
                    ])
            ],
            LyricsSyncMode.Word,
            LyricsSourceId.QqMusic,
            "item-42",
            LyricsFormat.Qrc);

        var result = new LyricsSearchResult(
            document,
            "item-42",
            "Title",
            "Artist",
            TimeSpan.FromSeconds(90),
            87,
            LyricsSourceId.QqMusic);

        var dto = RealtimeDtoMapper.ToLyricsDto(result);
        Assert.NotNull(dto);

        var envelope = RealtimeMessageSerializer.Create(
            RealtimeProtocol.TypeEvent,
            dto,
            id: "ly-1",
            name: RealtimeProtocol.EventLyricsUpdated,
            ts: 1_710_000_000_000);
        var json = RealtimeMessageSerializer.Serialize(envelope);
        var restoredMessage = RealtimeMessageSerializer.Deserialize(json);
        Assert.NotNull(restoredMessage);

        var restored = RealtimeMessageSerializer.DeserializePayload<RealtimeLyricsDto>(restoredMessage.Payload);
        Assert.NotNull(restored);
        Assert.Equal("item-42", restored.Id);
        Assert.Equal("Title", restored.Title);
        Assert.Equal("Artist", restored.Artist);
        Assert.Equal(90_000, restored.DurationMs);
        Assert.Equal(87, restored.Score);
        Assert.Equal(nameof(LyricsSourceId.QqMusic), restored.Source);
        Assert.NotNull(restored.Document);
        Assert.Equal(nameof(LyricsFormat.Qrc), restored.Document.Format);
        Assert.Equal(nameof(LyricsSyncMode.Word), restored.Document.SyncMode);
        Assert.Equal("item-42", restored.Document.ProviderItemId);
        Assert.Equal("Album", restored.Document.Metadata.Album);
        Assert.Equal(90_000, restored.Document.Metadata.DurationMs);
        Assert.Single(restored.Document.Lines);

        var line = restored.Document.Lines[0];
        Assert.Equal(100, line.StartMs);
        Assert.Equal(1100, line.EndMs);
        Assert.Equal("漢字", line.Text);
        Assert.Equal("hanzi", line.Translation);
        Assert.Equal("kanji", line.Romanization);
        Assert.True(line.IsDuet);
        Assert.Equal(2, line.Words.Count);
        Assert.Equal("漢", line.Words[0].Text);
        Assert.Equal(2, line.RubySpans.Count);
        Assert.Equal(0, line.RubySpans[0].BaseStart);
        Assert.Equal(1, line.RubySpans[0].BaseLength);
        Assert.Equal("かん", line.RubySpans[0].Reading);
    }

    [Fact]
    public void NullMediaAndNullLyrics_SerializeAsOmittedPayload()
    {
        var mediaMessage = RealtimeMessageSerializer.Create(
            RealtimeProtocol.TypeEvent,
            RealtimeDtoMapper.ToMediaDto(null, MediaInfoChangeKind.CurrentSession),
            name: RealtimeProtocol.EventMediaUpdated,
            ts: 1);
        var lyricsMessage = RealtimeMessageSerializer.Create(
            RealtimeProtocol.TypeEvent,
            RealtimeDtoMapper.ToLyricsDto(null),
            name: RealtimeProtocol.EventLyricsUpdated,
            ts: 2);

        var mediaJson = RealtimeMessageSerializer.Serialize(mediaMessage);
        var lyricsJson = RealtimeMessageSerializer.Serialize(lyricsMessage);

        Assert.Null(RealtimeMessageSerializer.Deserialize(mediaJson)!.Payload);
        Assert.Null(RealtimeMessageSerializer.Deserialize(lyricsJson)!.Payload);
        Assert.DoesNotContain("\"payload\":", mediaJson, StringComparison.Ordinal);
    }
}

