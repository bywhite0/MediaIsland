using MediaIsland.Helpers;
using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Lyrics.Models;
using MediaIsland.Services.Lyrics.Providers;
using MediaIsland.Services.Media;
using MediaIsland.Tests.Infrastructure;
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
    public void IsLineLyricsOnly_ReturnsTrue_WhenSourceFormatIsLrc()
    {
        var snapshot = new SPlayerNextLyricsClient.SPlayerLyricsSnapshot
        {
            Lyric =
            [
                new SPlayerNextLyricsClient.SPlayerLyricLine
                {
                    Words =
                    [
                        new SPlayerNextLyricsClient.SPlayerLyricWord { Word = "A", StartTime = 0, EndTime = 100 },
                        new SPlayerNextLyricsClient.SPlayerLyricWord { Word = "B", StartTime = 100, EndTime = 200 }
                    ]
                }
            ],
            Source = new SPlayerNextLyricsClient.SPlayerLyricSource { Format = "lrc" }
        };

        Assert.True(SPlayerNextLyricsClient.IsLineLyricsOnly(snapshot));
    }

    [Fact]
    public void IsLineLyricsOnly_ReturnsTrue_WhenEveryLineHasSingleWord()
    {
        var snapshot = new SPlayerNextLyricsClient.SPlayerLyricsSnapshot
        {
            Lyric =
            [
                new SPlayerNextLyricsClient.SPlayerLyricLine
                {
                    Words =
                    [
                        new SPlayerNextLyricsClient.SPlayerLyricWord { Word = "A", StartTime = 0, EndTime = 100 }
                    ]
                },
                new SPlayerNextLyricsClient.SPlayerLyricLine
                {
                    Words =
                    [
                        new SPlayerNextLyricsClient.SPlayerLyricWord { Word = "B", StartTime = 100, EndTime = 200 }
                    ]
                }
            ],
            Source = new SPlayerNextLyricsClient.SPlayerLyricSource { Format = null }
        };

        Assert.True(SPlayerNextLyricsClient.IsLineLyricsOnly(snapshot));
    }

    [Fact]
    public void IsLineLyricsOnly_ReturnsTrue_WhenEmptyWordLinesPresent()
    {
        var snapshot = new SPlayerNextLyricsClient.SPlayerLyricsSnapshot
        {
            Lyric =
            [
                new SPlayerNextLyricsClient.SPlayerLyricLine
                {
                    TranslatedLyric = "纯翻译行",
                    Words = null
                },
                new SPlayerNextLyricsClient.SPlayerLyricLine
                {
                    Words =
                    [
                        new SPlayerNextLyricsClient.SPlayerLyricWord { Word = "A", StartTime = 0, EndTime = 100 }
                    ]
                }
            ]
        };

        Assert.True(SPlayerNextLyricsClient.IsLineLyricsOnly(snapshot));
    }

    [Fact]
    public void IsLineLyricsOnly_ReturnsFalse_WhenSomeLineHasMultipleWords()
    {
        var snapshot = new SPlayerNextLyricsClient.SPlayerLyricsSnapshot
        {
            Lyric =
            [
                new SPlayerNextLyricsClient.SPlayerLyricLine
                {
                    Words =
                    [
                        new SPlayerNextLyricsClient.SPlayerLyricWord { Word = "A", StartTime = 0, EndTime = 100 }
                    ]
                },
                new SPlayerNextLyricsClient.SPlayerLyricLine
                {
                    Words =
                    [
                        new SPlayerNextLyricsClient.SPlayerLyricWord { Word = "B", StartTime = 100, EndTime = 150 },
                        new SPlayerNextLyricsClient.SPlayerLyricWord { Word = "C", StartTime = 150, EndTime = 200 }
                    ]
                }
            ],
            Source = new SPlayerNextLyricsClient.SPlayerLyricSource { Format = null }
        };

        Assert.False(SPlayerNextLyricsClient.IsLineLyricsOnly(snapshot));
    }

    [Fact]
    public void IsLineLyricsOnly_ReturnsFalse_ForWordSyncedYrcSource()
    {
        // YRC 是逐字格式（MapFormat 映射为 Lrc），按原始 format 字符串不应误判为逐行。
        var snapshot = new SPlayerNextLyricsClient.SPlayerLyricsSnapshot
        {
            Lyric =
            [
                new SPlayerNextLyricsClient.SPlayerLyricLine
                {
                    Words =
                    [
                        new SPlayerNextLyricsClient.SPlayerLyricWord { Word = "A", StartTime = 0, EndTime = 100 },
                        new SPlayerNextLyricsClient.SPlayerLyricWord { Word = "B", StartTime = 100, EndTime = 200 }
                    ]
                }
            ],
            Source = new SPlayerNextLyricsClient.SPlayerLyricSource { Format = "yrc" }
        };

        Assert.False(SPlayerNextLyricsClient.IsLineLyricsOnly(snapshot));
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

    [LiveServiceFact]
    public async Task TryFetchAsync_AgainstLiveApi_ReturnsDocumentWhenSourceMatches()
    {
        var client = new SPlayerNextLyricsClient();
        var settings = LyricsSourceSettings.Normalize(new LyricsSourceSettings
        {
            SPlayerNextApiBaseUrl = "http://127.0.0.1:14558"
        });

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        using var nowPlayingResponse = await GetNowPlayingOrFailAsync(http);

        await using var stream = await nowPlayingResponse.Content.ReadAsStreamAsync();
        using var document = await System.Text.Json.JsonDocument.ParseAsync(stream);
        var hasTrack = document.RootElement.TryGetProperty("track", out var track)
                       && track.ValueKind == System.Text.Json.JsonValueKind.Object;
        Assert.True(hasTrack, "前提不成立：now-playing 的应答里没有 track 对象，本机播放器可能没在放歌");

        var title = track.TryGetProperty("title", out var titleNode) ? titleNode.GetString() : null;
        Assert.False(
            string.IsNullOrWhiteSpace(title),
            "前提不成立：now-playing 的 track 没有标题，无法据此查歌词");

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

        // 与上面三处不同：这一条不是前提不成立，而是被测方法真的没返回文档。
        // 前提都已满足（服务在跑、有曲目、有标题），此时拿不到结果就是失败。
        // 原先这里也是 return，那让「客户端完全不工作」也报绿——
        // 这条测试因此从未真正验证过任何东西。
        Assert.NotNull(result);
        Assert.Equal(LyricsSourceId.SPlayerNext, result.Source);
        Assert.NotEmpty(result.Document.Lines);
        Assert.Equal(title, result.Title);
    }

    /// <summary>
    /// 探本机歌词服务。
    ///
    /// 门控已声明「服务在跑」是本条测试的前提，故服务不可达是前提不成立而非通过。
    /// 原先这里探测失败即 return，那报的是绿：它什么都没验证却声称通过，
    /// 而「全量 N passed」这句话里因此含有一条假的，且从计数上看不出来。
    ///
    /// 而且原先那道 IsSuccessStatusCode 检查根本走不到——服务不在跑时
    /// GetAsync 抛的是 HttpRequestException（连接被拒），不是返回非 2xx。
    /// 所以「服务不在跑就红」不是设计如此，是防护漏了异常路径。
    /// </summary>
    private static async Task<HttpResponseMessage> GetNowPlayingOrFailAsync(HttpClient http)
    {
        try
        {
            var response = await http.GetAsync("http://127.0.0.1:14558/api/now-playing");
            Assert.True(
                response.IsSuccessStatusCode,
                $"前提不成立：本机 14558 应答 {(int)response.StatusCode}");
            return response;
        }
        catch (HttpRequestException ex)
        {
            Assert.Fail(
                $"前提不成立：{LiveServiceFactAttribute.Variable} 已设为启用，" +
                $"但本机 14558 不可达（{ex.Message}）");
            throw;
        }
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

    private sealed class FixedSPlayerNextLyricsClient(LyricsSearchResult? result) : ISPlayerNextLyricsClient
    {
        public int CallCount { get; private set; }

        public Task<LyricsSearchResult?> TryFetchAsync(
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
