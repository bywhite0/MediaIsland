using System.Text.Json;
using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Lyrics.Models;
using MediaIsland.Services.Media;
using MediaIsland.Services.MediaLink.Mapping;
using MediaIsland.Services.MediaLink.Protocol;
using Xunit;

namespace MediaIsland.Tests.MediaLink;

public class MediaLinkDtoMapperTests
{
    [Fact]
    public void ToMediaDto_ReturnsNull_WhenMediaNull()
    {
        Assert.Null(MediaLinkDtoMapper.ToMediaDto(null, MediaInfoChangeKind.Timeline));
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

        var dto = MediaLinkDtoMapper.ToMediaDto(media, MediaInfoChangeKind.MediaProperties);
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
        Assert.NotNull(dto.TrackToken);
        Assert.NotEmpty(dto.TrackToken);

        var json = MediaLinkMessageSerializer.SerializePayload(dto);
        Assert.DoesNotContain("\"thumbnail\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"bitmap\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"hasThumbnail\":false", json, StringComparison.Ordinal);
        Assert.Contains("\"trackToken\":", json, StringComparison.Ordinal);
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

        var dto = MediaLinkDtoMapper.ToMediaDto(media, MediaInfoChangeKind.CurrentSession);
        Assert.NotNull(dto);
        Assert.True(dto.HasThumbnail);
    }

    [Fact]
    public void ToLyricsDto_ReturnsNull_WhenResultNull()
    {
        Assert.Null(MediaLinkDtoMapper.ToLyricsDto(null));
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

        var dto = MediaLinkDtoMapper.ToLyricsDto(result);
        Assert.NotNull(dto);
        // 未提供归属媒体时 trackToken 为 null（协议规定："无法确定归属则为 null"）。
        Assert.Null(dto.TrackToken);

        var envelope = MediaLinkMessageSerializer.Create(
            MediaLinkProtocol.TypeEvent,
            dto,
            id: "ly-1",
            name: MediaLinkProtocol.EventLyricsUpdated,
            ts: 1_710_000_000_000);
        var json = MediaLinkMessageSerializer.Serialize(envelope);
        var restoredMessage = MediaLinkMessageSerializer.Deserialize(json);
        Assert.NotNull(restoredMessage);

        var restored = MediaLinkMessageSerializer.DeserializePayload<MediaLinkLyricsDto>(restoredMessage.Payload);
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
        var mediaMessage = MediaLinkMessageSerializer.Create(
            MediaLinkProtocol.TypeEvent,
            MediaLinkDtoMapper.ToMediaDto(null, MediaInfoChangeKind.CurrentSession),
            name: MediaLinkProtocol.EventMediaUpdated,
            ts: 1);
        var lyricsMessage = MediaLinkMessageSerializer.Create(
            MediaLinkProtocol.TypeEvent,
            MediaLinkDtoMapper.ToLyricsDto(null),
            name: MediaLinkProtocol.EventLyricsUpdated,
            ts: 2);

        var mediaJson = MediaLinkMessageSerializer.Serialize(mediaMessage);
        var lyricsJson = MediaLinkMessageSerializer.Serialize(lyricsMessage);

        Assert.Null(MediaLinkMessageSerializer.Deserialize(mediaJson)!.Payload);
        Assert.Null(MediaLinkMessageSerializer.Deserialize(lyricsJson)!.Payload);
        Assert.DoesNotContain("\"payload\":", mediaJson, StringComparison.Ordinal);
    }

    [Fact]
    public void TrackToken_IsStable_ForSameTrackIdentity()
    {
        var media1 = CreateMediaInfo("Spotify.exe", "Song", "Artist");
        var media2 = CreateMediaInfo("Spotify.exe", "Song", "Artist");

        var dto1 = MediaLinkDtoMapper.ToMediaDto(media1, MediaInfoChangeKind.CurrentSession)!;
        var dto2 = MediaLinkDtoMapper.ToMediaDto(media2, MediaInfoChangeKind.CurrentSession)!;

        Assert.Equal(dto1.TrackToken, dto2.TrackToken);
    }

    [Fact]
    public void TrackToken_Changes_WhenTrackIdentityChanges()
    {
        var media1 = CreateMediaInfo("Spotify.exe", "Song A", "Artist");
        var media2 = CreateMediaInfo("Spotify.exe", "Song B", "Artist");

        var dto1 = MediaLinkDtoMapper.ToMediaDto(media1, MediaInfoChangeKind.CurrentSession)!;
        var dto2 = MediaLinkDtoMapper.ToMediaDto(media2, MediaInfoChangeKind.CurrentSession)!;

        Assert.NotEqual(dto1.TrackToken, dto2.TrackToken);
    }

    [Fact]
    public void TrackToken_SerializesInEnvelope()
    {
        var media = CreateMediaInfo("Spotify.exe", "Song", "Artist");
        var dto = MediaLinkDtoMapper.ToMediaDto(media, MediaInfoChangeKind.CurrentSession)!;

        var json = MediaLinkMessageSerializer.SerializePayload(dto);
        Assert.Contains("\"trackToken\":", json, StringComparison.Ordinal);

        var deserialized = MediaLinkMessageSerializer.DeserializePayload<MediaLinkMediaDto>(
            JsonDocument.Parse(json).RootElement);
        Assert.NotNull(deserialized);
        Assert.Equal(dto.TrackToken, deserialized.TrackToken);
    }

    [Fact]
    public void PositionCapturedAtMs_And_ServerTimeMs_SerializeWhenSet()
    {
        var dto = new MediaLinkMediaDto
        {
            ChangeKind = "Timeline",
            SourceApp = "test",
            PlaybackState = "Playing",
            TrackToken = "tok",
            PositionCapturedAtMs = 1_000_000,
            ServerTimeMs = 1_000_100
        };

        var json = MediaLinkMessageSerializer.SerializePayload(dto);
        Assert.Contains("\"positionCapturedAtMs\":1000000", json, StringComparison.Ordinal);
        Assert.Contains("\"serverTimeMs\":1000100", json, StringComparison.Ordinal);
    }

    [Fact]
    public void PositionCapturedAtMs_And_ServerTimeMs_OmitWhenDefault()
    {
        var dto = new MediaLinkMediaDto
        {
            ChangeKind = "Timeline",
            SourceApp = "test",
            PlaybackState = "Playing"
        };

        var json = MediaLinkMessageSerializer.SerializePayload(dto);
        Assert.DoesNotContain("positionCapturedAtMs", json, StringComparison.Ordinal);
        Assert.DoesNotContain("serverTimeMs", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ComputeTrackToken_DifferentSourceApps_DifferentTokens()
    {
        var tok1 = MediaLinkDtoMapper.ComputeTrackToken("Spotify.exe", "Song", "Artist");
        var tok2 = MediaLinkDtoMapper.ComputeTrackToken("vlc.exe", "Song", "Artist");
        Assert.NotEqual(tok1, tok2);
    }

    [Fact]
    public void ComputeTrackToken_NullTitleAndArtist_ProducesValidToken()
    {
        var tok = MediaLinkDtoMapper.ComputeTrackToken("app", null, null);
        Assert.NotNull(tok);
        Assert.NotEmpty(tok);
    }

    [Fact]
    public void LyricsTrackToken_MatchesMediaTrackToken_ForSameTrack()
    {
        // 协议核心契约：客户端按 lyrics.trackToken != media.trackToken 丢弃歌词，
        // 故同一曲目的两个频道必须算出同一个 token。
        var media = CreateMediaInfo("Spotify.exe", "Song", "Artist");
        var lyrics = CreateLyricsResult("Song", "Artist", LyricsSourceId.QqMusic);

        var mediaDto = MediaLinkDtoMapper.ToMediaDto(media, MediaInfoChangeKind.CurrentSession)!;
        var lyricsDto = MediaLinkDtoMapper.ToLyricsDto(lyrics, media)!;

        Assert.Equal(mediaDto.TrackToken, lyricsDto.TrackToken);
    }

    [Fact]
    public void LyricsTrackToken_IndependentOfLyricsSource()
    {
        // 歌词来源不同（QQ / 网易）但归属同一曲目时，token 必须一致。
        var media = CreateMediaInfo("Spotify.exe", "Song", "Artist");
        var fromQq = MediaLinkDtoMapper.ToLyricsDto(
            CreateLyricsResult("Song", "Artist", LyricsSourceId.QqMusic), media)!;
        var fromNetease = MediaLinkDtoMapper.ToLyricsDto(
            CreateLyricsResult("Song", "Artist", LyricsSourceId.Netease), media)!;

        Assert.Equal(fromQq.TrackToken, fromNetease.TrackToken);
    }

    [Fact]
    public void LyricsTrackToken_DiffersFromCurrentMedia_AfterTrackChange()
    {
        // 切歌后到达的歌词仍指向其所属旧曲目，客户端据此判定为陈旧。
        var oldMedia = CreateMediaInfo("Spotify.exe", "Old Song", "Artist");
        var newMedia = CreateMediaInfo("Spotify.exe", "New Song", "Artist");

        var lateLyrics = MediaLinkDtoMapper.ToLyricsDto(
            CreateLyricsResult("Old Song", "Artist", LyricsSourceId.QqMusic), oldMedia)!;
        var currentMedia = MediaLinkDtoMapper.ToMediaDto(newMedia, MediaInfoChangeKind.MediaProperties)!;

        Assert.NotEqual(currentMedia.TrackToken, lateLyrics.TrackToken);
    }

    [Fact]
    public void ComputeTrackToken_DistinguishesAlbumTitle()
    {
        var tok1 = MediaLinkDtoMapper.ComputeTrackToken("app", "Song", "Artist", "Album A");
        var tok2 = MediaLinkDtoMapper.ComputeTrackToken("app", "Song", "Artist", "Album B");
        Assert.NotEqual(tok1, tok2);
    }

    [Fact]
    public void ComputeTrackToken_IsSixteenLowerHexChars()
    {
        var tok = MediaLinkDtoMapper.ComputeTrackToken("app", "Song", "Artist", "Album");
        Assert.Equal(16, tok.Length);
        Assert.Matches("^[0-9a-f]{16}$", tok);
    }

    [Fact]
    public void ComputeTrackToken_SeparatorPreventsFieldAmbiguity()
    {
        // "ab"+"c" 与 "a"+"bc" 不得碰撞。
        var tok1 = MediaLinkDtoMapper.ComputeTrackToken("app", "ab", "c");
        var tok2 = MediaLinkDtoMapper.ComputeTrackToken("app", "a", "bc");
        Assert.NotEqual(tok1, tok2);
    }

    [Theory]
    [InlineData(LyricsSourceId.Netease, "Netease")]
    [InlineData(LyricsSourceId.QqMusic, "QqMusic")]
    [InlineData(LyricsSourceId.SPlayerNext, "SPlayerNext")]
    [InlineData(LyricsSourceId.External, "External")]
    [InlineData((LyricsSourceId)999, "Unknown")]
    public void MapLyricsSource_UnmappedValueDegradesToUnknown(LyricsSourceId source, string expected)
    {
        Assert.Equal(expected, MediaLinkDtoMapper.MapLyricsSource(source));
    }

    [Theory]
    [InlineData(LyricsFormat.Ttml, "Ttml")]
    [InlineData((LyricsFormat)999, "Unknown")]
    public void MapLyricsFormat_UnmappedValueDegradesToUnknown(LyricsFormat format, string expected)
    {
        Assert.Equal(expected, MediaLinkDtoMapper.MapLyricsFormat(format));
    }

    [Theory]
    [InlineData(LyricsSyncMode.Word, "Word")]
    [InlineData((LyricsSyncMode)999, "Unknown")]
    public void MapSyncMode_UnmappedValueDegradesToUnknown(LyricsSyncMode syncMode, string expected)
    {
        Assert.Equal(expected, MediaLinkDtoMapper.MapSyncMode(syncMode));
    }

    private static LyricsSearchResult CreateLyricsResult(string title, string artist, LyricsSourceId source) =>
        new(
            new LyricsDocument(
                new LyricsMetadata(title, artist, null, TimeSpan.FromSeconds(90)),
                [],
                LyricsSyncMode.Line,
                source,
                "item",
                LyricsFormat.Lrc),
            "item",
            title,
            artist,
            TimeSpan.FromSeconds(90),
            80,
            source);

    [Fact]
    public void MapPlaybackState_KnownValues_MatchEnumNames()
    {
        Assert.Equal("Unknown", MediaLinkDtoMapper.MapPlaybackState(MediaPlaybackState.Unknown));
        Assert.Equal("Playing", MediaLinkDtoMapper.MapPlaybackState(MediaPlaybackState.Playing));
        Assert.Equal("Paused", MediaLinkDtoMapper.MapPlaybackState(MediaPlaybackState.Paused));
        Assert.Equal("Stopped", MediaLinkDtoMapper.MapPlaybackState(MediaPlaybackState.Stopped));
        Assert.Equal("Closed", MediaLinkDtoMapper.MapPlaybackState(MediaPlaybackState.Closed));
        Assert.Equal("Opened", MediaLinkDtoMapper.MapPlaybackState(MediaPlaybackState.Opened));
        Assert.Equal("Changing", MediaLinkDtoMapper.MapPlaybackState(MediaPlaybackState.Changing));
    }

    [Fact]
    public void MapPlaybackState_UnknownEnumValue_ReturnsUnknown()
    {
        var bad = (MediaPlaybackState)999;
        Assert.Equal("Unknown", MediaLinkDtoMapper.MapPlaybackState(bad));
    }

    [Fact]
    public void MapChangeKind_KnownValues_MatchEnumNames()
    {
        Assert.Equal("CurrentSession", MediaLinkDtoMapper.MapChangeKind(MediaInfoChangeKind.CurrentSession));
        Assert.Equal("MediaProperties", MediaLinkDtoMapper.MapChangeKind(MediaInfoChangeKind.MediaProperties));
        Assert.Equal("Playback", MediaLinkDtoMapper.MapChangeKind(MediaInfoChangeKind.Playback));
        Assert.Equal("Timeline", MediaLinkDtoMapper.MapChangeKind(MediaInfoChangeKind.Timeline));
    }

    [Fact]
    public void MapChangeKind_UnknownEnumValue_ReturnsUnknown()
    {
        var bad = (MediaInfoChangeKind)999;
        Assert.Equal("Unknown", MediaLinkDtoMapper.MapChangeKind(bad));
    }
    private static MediaInfo CreateMediaInfo(string sourceApp, string? title, string? artist) =>
        new(sourceApp, title, artist, null, TimeSpan.Zero, TimeSpan.Zero,
            new MediaPlaybackInfo(MediaPlaybackState.Playing), null, null);
}