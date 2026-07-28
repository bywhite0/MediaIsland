using MediaIsland.Helpers;
using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Lyrics.Models;
using MediaIsland.Services.Lyrics.Providers;
using MediaIsland.Services.Media;
using Xunit;

namespace MediaIsland.Tests.Lyrics;

public class SPlayerNextLyricsClientTests
{
    [Fact]
    public void MapFormat_MapsKnownFormats()
    {
        Assert.Equal(LyricsFormat.Ttml, SPlayerNextLyricsClient.MapFormat("ttml"));
        Assert.Equal(LyricsFormat.Qrc, SPlayerNextLyricsClient.MapFormat("QRC"));
        Assert.Equal(LyricsFormat.Krc, SPlayerNextLyricsClient.MapFormat("krc"));
        Assert.Equal(LyricsFormat.Lrc, SPlayerNextLyricsClient.MapFormat("yrc"));
        Assert.Equal(LyricsFormat.Unknown, SPlayerNextLyricsClient.MapFormat(null));
    }

    [Fact]
    public void ConvertLines_BuildsWordSyncedLinesWithTranslationAndOffset()
    {
        var source = new List<SPlayerNextLyricsClient.SPlayerLyricLine>
        {
            new()
            {
                StartTime = 1000,
                EndTime = 2000,
                TranslatedLyric = "翻译",
                RomanLyric = "roma",
                IsBG = false,
                IsDuet = true,
                Words =
                [
                    new SPlayerNextLyricsClient.SPlayerLyricWord
                    {
                        Word = "Hello ",
                        StartTime = 1000,
                        EndTime = 1500
                    },
                    new SPlayerNextLyricsClient.SPlayerLyricWord
                    {
                        Word = "World",
                        StartTime = 1500,
                        EndTime = 2000
                    }
                ]
            }
        };

        var lines = SPlayerNextLyricsClient.ConvertLines(source, lyricOffsetMs: 200);
        var line = Assert.Single(lines);
        Assert.Equal("Hello World", line.Text);
        Assert.Equal("翻译", line.Translation);
        Assert.Equal("roma", line.Romanization);
        Assert.True(line.IsDuet);
        Assert.Equal(TimeSpan.FromMilliseconds(800), line.StartTime);
        Assert.Equal(TimeSpan.FromMilliseconds(1800), line.EndTime);
        Assert.Equal(2, line.Words.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(800), line.Words[0].StartTime);
        Assert.Equal(TimeSpan.FromMilliseconds(1300), line.Words[0].EndTime);
    }

    [Fact]
    public void IsSameTrack_MatchesTitleAndOverlappingArtists()
    {
        var media = new MediaInfo(
            SPlayerNextMediaSource.SourceAppId,
            "ME!",
            "Taylor Swift / Brendon Urie",
            "Lover",
            TimeSpan.Zero,
            TimeSpan.FromSeconds(180),
            new MediaPlaybackInfo(MediaPlaybackState.Playing),
            null,
            null);
        var track = new SPlayerNextLyricsClient.SPlayerTrack
        {
            Title = "ME!",
            Artists =
            [
                new SPlayerNextLyricsClient.SPlayerArtist { Name = "Taylor Swift" },
                new SPlayerNextLyricsClient.SPlayerArtist { Name = "Brendon Urie" }
            ]
        };

        Assert.True(SPlayerNextLyricsClient.IsSameTrack(media, track));
        Assert.False(SPlayerNextLyricsClient.IsSameTrack(
            media with { Title = "Other Song" },
            track));
    }

    [Fact]
    public void NormalizeSPlayerNextBaseUrl_UsesDefaultAndStripsApiSuffix()
    {
        Assert.Equal(
            LyricsSourceSettings.DefaultSPlayerNextApiBaseUrl,
            LyricsSourceSettings.NormalizeSPlayerNextBaseUrl(null));
        Assert.Equal(
            LyricsSourceSettings.DefaultSPlayerNextApiBaseUrl,
            LyricsSourceSettings.NormalizeSPlayerNextBaseUrl("not-a-url"));
        Assert.Equal(
            "http://127.0.0.1:14558",
            LyricsSourceSettings.NormalizeSPlayerNextBaseUrl("http://127.0.0.1:14558/api/"));
        Assert.Equal(
            "http://127.0.0.1:24558",
            LyricsSourceSettings.NormalizeSPlayerNextBaseUrl("http://127.0.0.1:24558/"));
    }

    [Fact]
    public async Task TryFetchAsync_AgainstLiveApi_ReturnsDocumentWhenSourceMatches()
    {
        var client = new SPlayerNextLyricsClient();
        var settings = LyricsSourceSettings.Normalize(new LyricsSourceSettings
        {
            SPlayerNextApiBaseUrl = "http://127.0.0.1:14558"
        });

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        using var nowPlayingResponse = await http.GetAsync("http://127.0.0.1:14558/api/now-playing");
        if (!nowPlayingResponse.IsSuccessStatusCode)
        {
            return;
        }

        await using var stream = await nowPlayingResponse.Content.ReadAsStreamAsync();
        using var document = await System.Text.Json.JsonDocument.ParseAsync(stream);
        if (!document.RootElement.TryGetProperty("track", out var track) ||
            track.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return;
        }

        var title = track.TryGetProperty("title", out var titleNode) ? titleNode.GetString() : null;
        if (string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        var artists = new List<string>();
        if (track.TryGetProperty("artists", out var artistsNode) &&
            artistsNode.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var artist in artistsNode.EnumerateArray())
            {
                if (artist.TryGetProperty("name", out var nameNode))
                {
                    var name = nameNode.GetString();
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        artists.Add(name);
                    }
                }
            }
        }

        var media = new MediaInfo(
            SPlayerNextMediaSource.SourceAppId,
            title,
            string.Join(" / ", artists),
            null,
            TimeSpan.Zero,
            TimeSpan.Zero,
            new MediaPlaybackInfo(MediaPlaybackState.Playing),
            null,
            null);

        var result = await client.TryFetchAsync(media, settings, CancellationToken.None);
        if (result == null)
        {
            return;
        }

        Assert.Equal(LyricsSourceId.SPlayerNext, result.Source);
        Assert.NotEmpty(result.Document.Lines);
        Assert.Equal(title, result.Title);
    }
}

public class SPlayerNextLyricsSearchIntegrationTests
{
    [Fact]
    public async Task SearchAsync_UsesSPlayerNextDirectPath_AndSkipsProviders()
    {
        var provider = new CountingProvider();
        var direct = CreateDirectResult();
        var client = new FixedSPlayerNextLyricsClient(direct);
        var service = new LyricsSearchService(
            [provider],
            [new PassthroughParser()],
            () => new LyricsSourceSettings
            {
                Sources =
                [
                    new LyricsSourceEntry
                    {
                        Id = LyricsSourceId.QqMusic,
                        IsEnabled = true,
                        UseWordSyncedLyrics = true
                    }
                ]
            },
            sPlayerNextLyricsClient: client);

        var result = await service.SearchAsync(CreateSPlayerMedia());

        Assert.NotNull(result);
        Assert.Equal(LyricsSourceId.SPlayerNext, result.Source);
        Assert.Equal(1, client.CallCount);
        Assert.Equal(0, provider.SearchCallCount);
    }

    [Fact]
    public async Task SearchAsync_FallsBackToProviders_WhenSPlayerNextHasNoLyrics()
    {
        var provider = new CountingProvider();
        var client = new FixedSPlayerNextLyricsClient(null);
        var service = new LyricsSearchService(
            [provider],
            [new PassthroughParser()],
            () => new LyricsSourceSettings
            {
                Sources =
                [
                    new LyricsSourceEntry
                    {
                        Id = LyricsSourceId.QqMusic,
                        IsEnabled = true,
                        UseWordSyncedLyrics = true
                    }
                ]
            },
            sPlayerNextLyricsClient: client);

        var result = await service.SearchAsync(CreateSPlayerMedia(), allowProviderSearch: true);

        Assert.NotNull(result);
        Assert.Equal(LyricsSourceId.QqMusic, result.Source);
        Assert.Equal(1, client.CallCount);
        Assert.Equal(1, provider.SearchCallCount);
    }

    [Fact]
    public async Task SearchAsync_DoesNotFallback_WhenProviderSearchDisabled()
    {
        var provider = new CountingProvider();
        var client = new FixedSPlayerNextLyricsClient(null);
        var service = new LyricsSearchService(
            [provider],
            [new PassthroughParser()],
            () => new LyricsSourceSettings
            {
                Sources =
                [
                    new LyricsSourceEntry
                    {
                        Id = LyricsSourceId.QqMusic,
                        IsEnabled = true,
                        UseWordSyncedLyrics = true
                    }
                ]
            },
            sPlayerNextLyricsClient: client);

        var result = await service.SearchAsync(CreateSPlayerMedia(), allowProviderSearch: false);

        Assert.Null(result);
        Assert.Equal(1, client.CallCount);
        Assert.Equal(0, provider.SearchCallCount);
    }

    [Fact]
    public async Task SearchAsync_DoesNotQuerySPlayerClient_ForOtherSources()
    {
        var provider = new CountingProvider();
        var client = new FixedSPlayerNextLyricsClient(CreateDirectResult());
        var service = new LyricsSearchService(
            [provider],
            [new PassthroughParser()],
            () => new LyricsSourceSettings
            {
                Sources =
                [
                    new LyricsSourceEntry
                    {
                        Id = LyricsSourceId.QqMusic,
                        IsEnabled = true,
                        UseWordSyncedLyrics = true
                    }
                ]
            },
            sPlayerNextLyricsClient: client);

        var media = CreateSPlayerMedia() with { SourceApp = "Spotify.exe" };
        var result = await service.SearchAsync(media);

        Assert.NotNull(result);
        Assert.Equal(LyricsSourceId.QqMusic, result.Source);
        Assert.Equal(0, client.CallCount);
        Assert.Equal(1, provider.SearchCallCount);
    }

    private static MediaInfo CreateSPlayerMedia() => new(
        SPlayerNextMediaSource.SourceAppId,
        "Song",
        "Artist",
        "Album",
        TimeSpan.Zero,
        TimeSpan.FromSeconds(180),
        new MediaPlaybackInfo(MediaPlaybackState.Playing),
        null,
        null);

    private static LyricsSearchResult CreateDirectResult()
    {
        var document = LyricsDocumentNormalizer.Create(
        [
            new LyricsLine(
                TimeSpan.FromMilliseconds(0),
                TimeSpan.FromMilliseconds(1000),
                "direct",
                [new LyricsWord(TimeSpan.Zero, TimeSpan.FromMilliseconds(1000), "direct")])
        ],
        new LyricsMetadata("Song", "Artist", "Album", TimeSpan.FromSeconds(180)),
        LyricsSourceId.SPlayerNext,
        "splayer-track",
        LyricsFormat.Lrc,
        preferWordSync: true);

        return new LyricsSearchResult(
            document,
            document.ProviderItemId,
            "Song",
            "Artist",
            TimeSpan.FromSeconds(180),
            1000,
            LyricsSourceId.SPlayerNext);
    }

    private sealed class FixedSPlayerNextLyricsClient(LyricsSearchResult? result) : SPlayerNextLyricsClient
    {
        public int CallCount { get; private set; }

        public override Task<LyricsSearchResult?> TryFetchAsync(
            MediaInfo media,
            LyricsSourceSettings settings,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class CountingProvider : ILyricsProvider
    {
        public int SearchCallCount { get; private set; }

        public LyricsSourceId Id => LyricsSourceId.QqMusic;

        public Task<IReadOnlyList<LyricsCandidate>> SearchAsync(
            MediaInfo media,
            LyricsSourceSettings settings,
            CancellationToken cancellationToken)
        {
            SearchCallCount++;
            IReadOnlyList<LyricsCandidate> candidates =
            [
                new LyricsCandidate(
                    Id,
                    "qq-1",
                    media.Title ?? string.Empty,
                    media.Artist ?? string.Empty,
                    media.AlbumTitle ?? string.Empty,
                    media.Duration,
                    150,
                    true)
            ];
            return Task.FromResult(candidates);
        }

        public Task<LyricsPayload?> FetchAsync(
            LyricsCandidate candidate,
            LyricsSourceSettings settings,
            CancellationToken cancellationToken) =>
            Task.FromResult<LyricsPayload?>(new LyricsPayload(
                LyricsFormat.Qrc,
                "payload",
                Id,
                candidate.ProviderItemId,
                new LyricsMetadata(candidate.Title, candidate.Artist, candidate.Album, candidate.Duration)));
    }

    private sealed class PassthroughParser : ILyricsPayloadParser
    {
        public bool CanParse(LyricsFormat format) => true;

        public ValueTask<LyricsDocument> ParseAsync(LyricsPayload payload, CancellationToken cancellationToken)
        {
            var document = LyricsDocumentNormalizer.Create(
            [
                new LyricsLine(
                    TimeSpan.Zero,
                    TimeSpan.FromSeconds(1),
                    "Song",
                    [new LyricsWord(TimeSpan.Zero, TimeSpan.FromSeconds(1), "Song")])
            ],
            payload.Metadata,
            payload.Source,
            payload.ProviderItemId,
            payload.Format,
            preferWordSync: true);
            return ValueTask.FromResult(document);
        }
    }
}
