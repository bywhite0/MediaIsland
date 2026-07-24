using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Lyrics.Models;
using MediaIsland.Services.Media;
using Xunit;

namespace MediaIsland.Tests.Lyrics;

public class LyricsSearchServiceTests
{
    [Fact]
    public async Task SearchAsync_PrefersLaterWordSyncedSource_OverEarlierLineSource()
    {
        var settings = CreateSettings();
        var service = new LyricsSearchService(
        [
            new FakeProvider(LyricsSourceId.Netease, LyricsFormat.Lrc, supportsWordSync: false),
            new FakeProvider(LyricsSourceId.QqMusic, LyricsFormat.Qrc, supportsWordSync: true)
        ],
        [new FakeParser()],
        () => settings);

        var result = await service.SearchAsync(CreateMediaInfo());

        Assert.NotNull(result);
        Assert.Equal(LyricsSourceId.QqMusic, result.Source);
        Assert.Equal(LyricsSyncMode.Word, result.Document.SyncMode);
    }

    [Fact]
    public async Task SearchAsync_UsesEarlierLineSource_WhenNoWordPayloadSucceeds()
    {
        var settings = CreateSettings();
        var service = new LyricsSearchService(
        [
            new FakeProvider(LyricsSourceId.Netease, LyricsFormat.Lrc, supportsWordSync: false),
            new FakeProvider(LyricsSourceId.QqMusic, LyricsFormat.Qrc, supportsWordSync: true, returnsPayload: false)
        ],
        [new FakeParser()],
        () => settings);

        var result = await service.SearchAsync(CreateMediaInfo());

        Assert.NotNull(result);
        Assert.Equal(LyricsSourceId.Netease, result.Source);
        Assert.Equal(LyricsSyncMode.Line, result.Document.SyncMode);
    }

    [Fact]
    public async Task SearchAsync_PrefersLineSyncedSource_WhenWordSyncIsDisabled()
    {
        var settings = new LyricsSourceSettings
        {
            Sources =
            [
                new LyricsSourceEntry
                {
                    Id = LyricsSourceId.Kugou,
                    IsEnabled = true,
                    UseWordSyncedLyrics = false
                },
                new LyricsSourceEntry
                {
                    Id = LyricsSourceId.Netease,
                    IsEnabled = true,
                    UseWordSyncedLyrics = false
                }
            ]
        };
        var service = new LyricsSearchService(
        [
            new FakeProvider(LyricsSourceId.Kugou, LyricsFormat.Krc, supportsWordSync: true),
            new FakeProvider(LyricsSourceId.Netease, LyricsFormat.Lrc, supportsWordSync: false)
        ],
        [new FakeParser()],
        () => settings);

        var result = await service.SearchAsync(CreateMediaInfo());

        Assert.NotNull(result);
        Assert.Equal(LyricsSourceId.Netease, result.Source);
        Assert.Equal(LyricsSyncMode.Line, result.Document.SyncMode);
    }

    [Fact]
    public async Task SearchAsync_PublishesCurrentSelectedSource()
    {
        var settings = CreateSettings();
        var service = new LyricsSearchService(
        [
            new FakeProvider(LyricsSourceId.QqMusic, LyricsFormat.Qrc, supportsWordSync: true)
        ],
        [new FakeParser()],
        () => settings);
        LyricsSearchResult? published = null;
        service.CurrentResultChanged += (_, args) => published = args.Result;

        var result = await service.SearchAsync(CreateMediaInfo());

        Assert.Same(result, service.CurrentResult);
        Assert.Same(result, published);
        Assert.Equal(LyricsSourceId.QqMusic, published?.Source);
        Assert.Same(result, service.GetCurrentResultFor(CreateMediaInfo()));
        Assert.Null(service.GetCurrentResultFor(CreateMediaInfo() with { Title = "Other Song" }));
    }

    [Fact]
    public async Task SearchAsync_ContinuesAfterProviderTimeout()
    {
        var settings = CreateSettings();
        var service = new LyricsSearchService(
        [
            new TimeoutProvider(LyricsSourceId.Netease),
            new FakeProvider(LyricsSourceId.QqMusic, LyricsFormat.Qrc, supportsWordSync: true)
        ],
        [new FakeParser()],
        () => settings);

        var result = await service.SearchAsync(CreateMediaInfo());

        Assert.NotNull(result);
        Assert.Equal(LyricsSourceId.QqMusic, result.Source);
    }

    [Fact]
    public async Task SearchAsync_WaitsForFirstSourceToFinishBeforeTryingLaterSources()
    {
        var settings = CreateSettings();
        var firstProvider = new BlockingEmptyProvider(LyricsSourceId.Netease);
        var laterProvider = new FakeProvider(LyricsSourceId.QqMusic, LyricsFormat.Qrc, supportsWordSync: true);
        var service = new LyricsSearchService(
        [firstProvider, laterProvider],
        [new FakeParser()],
        () => settings);

        var searchTask = service.SearchAsync(CreateMediaInfo());
        await firstProvider.SearchStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, laterProvider.SearchCallCount);

        firstProvider.CompleteSearch();
        var result = await searchTask;

        Assert.NotNull(result);
        Assert.Equal(LyricsSourceId.QqMusic, result.Source);
        Assert.Equal(1, laterProvider.SearchCallCount);
    }

    [Fact]
    public async Task SearchAsync_DoesNotRetryEmptyProviderWithinSingleSearch()
    {
        var settings = new LyricsSourceSettings
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
        };
        var provider = new FakeProvider(
            LyricsSourceId.QqMusic,
            LyricsFormat.Qrc,
            supportsWordSync: true,
            emptySearchCount: 2);
        var service = new LyricsSearchService([provider], [new FakeParser()], () => settings);

        var result = await service.SearchAsync(CreateMediaInfo());

        Assert.Null(result);
        Assert.Equal(1, provider.SearchCallCount);
    }

    [Fact]
    public async Task SearchAsync_SkipsDisabledAmllAndUsesOtherEnabledSources()
    {
        var settings = new LyricsSourceSettings
        {
            Sources =
            [
                new LyricsSourceEntry
                {
                    Id = LyricsSourceId.AmllTtml,
                    IsEnabled = false,
                    UseWordSyncedLyrics = true
                },
                new LyricsSourceEntry
                {
                    Id = LyricsSourceId.QqMusic,
                    IsEnabled = true,
                    UseWordSyncedLyrics = true
                }
            ]
        };
        var amll = new ThrowingProvider(LyricsSourceId.AmllTtml);
        var qqMusic = new FakeProvider(
            LyricsSourceId.QqMusic,
            LyricsFormat.Qrc,
            supportsWordSync: true);
        var service = new LyricsSearchService([amll, qqMusic], [new FakeParser()], () => settings);

        var result = await service.SearchAsync(CreateMediaInfo());

        Assert.NotNull(result);
        Assert.Equal(0, amll.SearchCallCount);
        Assert.Equal(1, qqMusic.SearchCallCount);
        Assert.Equal(LyricsSourceId.QqMusic, result.Source);
    }

    [Fact]
    public async Task SearchAsync_DoesNotRetryAfterProviderTimeout()
    {
        var settings = new LyricsSourceSettings
        {
            Sources =
            [
                new LyricsSourceEntry
                {
                    Id = LyricsSourceId.AmllTtml,
                    IsEnabled = true,
                    UseWordSyncedLyrics = true
                }
            ]
        };
        var amll = new TimeoutProvider(LyricsSourceId.AmllTtml);
        var service = new LyricsSearchService([amll], [new FakeParser()], () => settings);

        var result = await service.SearchAsync(CreateMediaInfo());

        Assert.Null(result);
    }

    [Fact]
    public async Task SearchAsync_PropagatesRequestedCancellation()
    {
        var settings = CreateSettings();
        var service = new LyricsSearchService(
        [new FakeProvider(LyricsSourceId.QqMusic, LyricsFormat.Qrc, supportsWordSync: true)],
        [new FakeParser()],
        () => settings);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.SearchAsync(CreateMediaInfo(), cancellation.Token));
    }

    [Fact]
    public async Task SearchCandidatesAsync_ReusesProviderCandidatesFromSearchAsync()
    {
        var settings = new LyricsSourceSettings
        {
            Sources =
            [
                new LyricsSourceEntry
                {
                    Id = LyricsSourceId.Netease,
                    IsEnabled = true,
                    UseWordSyncedLyrics = false
                },
                new LyricsSourceEntry
                {
                    Id = LyricsSourceId.QqMusic,
                    IsEnabled = true,
                    UseWordSyncedLyrics = true
                }
            ]
        };
        var netease = new FakeProvider(LyricsSourceId.Netease, LyricsFormat.Lrc, supportsWordSync: false, score: 40);
        var qqMusic = new FakeProvider(LyricsSourceId.QqMusic, LyricsFormat.Qrc, supportsWordSync: true, score: 150);
        var service = new LyricsSearchService([netease, qqMusic], [new FakeParser()], () => settings);
        var media = CreateMediaInfo();

        var result = await service.SearchAsync(media);
        var candidates = await service.SearchCandidatesAsync(media);

        Assert.NotNull(result);
        Assert.Equal(LyricsSourceId.QqMusic, result.Source);
        Assert.Equal(2, candidates.Count);
        Assert.Equal(1, netease.SearchCallCount);
        Assert.Equal(1, qqMusic.SearchCallCount);
    }

    [Fact]
    public async Task SearchCandidatesAsync_DoesNotResearchProvidersOnRepeatedCalls()
    {
        var settings = new LyricsSourceSettings
        {
            Sources =
            [
                new LyricsSourceEntry { Id = LyricsSourceId.Netease, IsEnabled = true },
                new LyricsSourceEntry { Id = LyricsSourceId.QqMusic, IsEnabled = true }
            ]
        };
        var netease = new FakeProvider(LyricsSourceId.Netease, LyricsFormat.Lrc, supportsWordSync: false, score: 40);
        var qqMusic = new FakeProvider(LyricsSourceId.QqMusic, LyricsFormat.Qrc, supportsWordSync: true, score: 150);
        var service = new LyricsSearchService([netease, qqMusic], [new FakeParser()], () => settings);
        var media = CreateMediaInfo();

        var first = await service.SearchCandidatesAsync(media);
        var second = await service.SearchCandidatesAsync(media);

        Assert.Equal(2, first.Count);
        Assert.Equal(2, second.Count);
        Assert.Equal(1, netease.SearchCallCount);
        Assert.Equal(1, qqMusic.SearchCallCount);
    }

    [Fact]
    public async Task SearchCandidatesAsync_ReturnsAllEnabledSourceCandidatesSortedByScore()
    {
        var settings = new LyricsSourceSettings
        {
            Sources =
            [
                new LyricsSourceEntry { Id = LyricsSourceId.Netease, IsEnabled = true },
                new LyricsSourceEntry { Id = LyricsSourceId.QqMusic, IsEnabled = true },
                new LyricsSourceEntry { Id = LyricsSourceId.Kugou, IsEnabled = false }
            ]
        };
        var netease = new FakeProvider(LyricsSourceId.Netease, LyricsFormat.Lrc, supportsWordSync: false, score: 40);
        var qqMusic = new FakeProvider(LyricsSourceId.QqMusic, LyricsFormat.Qrc, supportsWordSync: true, score: 150);
        var kugou = new ThrowingProvider(LyricsSourceId.Kugou);
        var service = new LyricsSearchService([netease, qqMusic, kugou], [new FakeParser()], () => settings);

        var candidates = await service.SearchCandidatesAsync(CreateMediaInfo());

        Assert.Collection(
            candidates,
            candidate =>
            {
                Assert.Equal(LyricsSourceId.QqMusic, candidate.Source);
                Assert.Equal(150, candidate.Score);
            },
            candidate =>
            {
                Assert.Equal(LyricsSourceId.Netease, candidate.Source);
                Assert.Equal(40, candidate.Score);
            });
        Assert.Equal(1, netease.SearchCallCount);
        Assert.Equal(1, qqMusic.SearchCallCount);
        Assert.Equal(0, kugou.SearchCallCount);
    }

    [Fact]
    public async Task ApplyCandidateAsync_PublishesTheSelectedCandidate()
    {
        var settings = CreateSettings();
        var provider = new FakeProvider(LyricsSourceId.QqMusic, LyricsFormat.Qrc, supportsWordSync: true);
        var service = new LyricsSearchService([provider], [new FakeParser()], () => settings);
        LyricsSearchResult? applied = null;
        service.CandidateApplied += (_, args) => applied = args.Result;
        var media = CreateMediaInfo();
        var candidate = new LyricsCandidate(
            LyricsSourceId.QqMusic,
            "manual-choice",
            "Manual Song",
            "Manual Artist",
            "Manual Album",
            TimeSpan.FromSeconds(180),
            40,
            SupportsWordSync: true);

        var result = await service.ApplyCandidateAsync(media, candidate);

        Assert.NotNull(result);
        Assert.Equal("manual-choice", result.Id);
        Assert.Equal(LyricsSourceId.QqMusic, result.Source);
        Assert.Same(result, service.CurrentResult);
        Assert.Same(result, applied);
    }

    private static LyricsSourceSettings CreateSettings() => new()
    {
        Sources =
        [
            new LyricsSourceEntry
            {
                Id = LyricsSourceId.Netease,
                IsEnabled = true,
                UseWordSyncedLyrics = false
            },
            new LyricsSourceEntry
            {
                Id = LyricsSourceId.QqMusic,
                IsEnabled = true,
                UseWordSyncedLyrics = true
            }
        ]
    };

    private static MediaInfo CreateMediaInfo() => new(
        "Spotify.exe",
        "Song",
        "Artist",
        "Album",
        TimeSpan.Zero,
        TimeSpan.FromSeconds(180),
        new MediaPlaybackInfo(MediaPlaybackState.Playing),
        null,
        null);

    private sealed class FakeProvider(
        LyricsSourceId id,
        LyricsFormat format,
        bool supportsWordSync,
        bool returnsPayload = true,
        int emptySearchCount = 0,
        int score = 150) : ILyricsProvider
    {
        public int SearchCallCount { get; private set; }

        public LyricsSourceId Id => id;

        public Task<IReadOnlyList<LyricsCandidate>> SearchAsync(
            MediaInfo media,
            LyricsSourceSettings settings,
            CancellationToken cancellationToken)
        {
            SearchCallCount++;
            if (SearchCallCount <= emptySearchCount)
            {
                return Task.FromResult<IReadOnlyList<LyricsCandidate>>([]);
            }

            IReadOnlyList<LyricsCandidate> candidates =
            [
                new LyricsCandidate(
                    id,
                    id.ToString(),
                    media.Title ?? string.Empty,
                    media.Artist ?? string.Empty,
                    media.AlbumTitle ?? string.Empty,
                    media.Duration,
                    score,
                    supportsWordSync)
            ];
            return Task.FromResult(candidates);
        }

        public Task<LyricsPayload?> FetchAsync(
            LyricsCandidate candidate,
            LyricsSourceSettings settings,
            CancellationToken cancellationToken)
        {
            LyricsPayload? payload = returnsPayload
                ? new LyricsPayload(
                    format,
                    "payload",
                    id,
                    candidate.ProviderItemId,
                    new LyricsMetadata(candidate.Title, candidate.Artist, candidate.Album, candidate.Duration))
                : null;
            return Task.FromResult(payload);
        }
    }

    private sealed class FakeParser : ILyricsPayloadParser
    {
        public bool CanParse(LyricsFormat format) => true;

        public ValueTask<LyricsDocument> ParseAsync(
            LyricsPayload payload,
            CancellationToken cancellationToken)
        {
            var isWordSynced = payload.Format is LyricsFormat.Qrc or LyricsFormat.Krc;
            IReadOnlyList<LyricsWord> words = isWordSynced
                ? [new LyricsWord(TimeSpan.Zero, TimeSpan.FromSeconds(1), "Song")]
                : [];
            var document = LyricsDocumentNormalizer.Create(
            [
                new LyricsLine(TimeSpan.Zero, TimeSpan.FromSeconds(1), "Song", words)
            ],
            payload.Metadata,
            payload.Source,
            payload.ProviderItemId,
            payload.Format,
            preferWordSync: isWordSynced);
            return ValueTask.FromResult(document);
        }
    }

    private sealed class TimeoutProvider(LyricsSourceId id) : ILyricsProvider
    {
        public LyricsSourceId Id => id;

        public Task<IReadOnlyList<LyricsCandidate>> SearchAsync(
            MediaInfo media,
            LyricsSourceSettings settings,
            CancellationToken cancellationToken) =>
            Task.FromException<IReadOnlyList<LyricsCandidate>>(new TaskCanceledException("provider timeout"));

        public Task<LyricsPayload?> FetchAsync(
            LyricsCandidate candidate,
            LyricsSourceSettings settings,
            CancellationToken cancellationToken) =>
            Task.FromResult<LyricsPayload?>(null);
    }

    private sealed class BlockingEmptyProvider(LyricsSourceId id) : ILyricsProvider
    {
        private readonly TaskCompletionSource _searchCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SearchStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public LyricsSourceId Id => id;

        public async Task<IReadOnlyList<LyricsCandidate>> SearchAsync(
            MediaInfo media,
            LyricsSourceSettings settings,
            CancellationToken cancellationToken)
        {
            SearchStarted.TrySetResult();
            await _searchCompleted.Task.WaitAsync(cancellationToken);
            return [];
        }

        public Task<LyricsPayload?> FetchAsync(
            LyricsCandidate candidate,
            LyricsSourceSettings settings,
            CancellationToken cancellationToken) =>
            Task.FromResult<LyricsPayload?>(null);

        public void CompleteSearch() => _searchCompleted.TrySetResult();
    }

    private sealed class ThrowingProvider(LyricsSourceId id) : ILyricsProvider
    {
        public int SearchCallCount { get; private set; }

        public LyricsSourceId Id => id;

        public Task<IReadOnlyList<LyricsCandidate>> SearchAsync(
            MediaInfo media,
            LyricsSourceSettings settings,
            CancellationToken cancellationToken)
        {
            SearchCallCount++;
            throw new InvalidOperationException("This provider should not be queried.");
        }

        public Task<LyricsPayload?> FetchAsync(
            LyricsCandidate candidate,
            LyricsSourceSettings settings,
            CancellationToken cancellationToken) =>
            Task.FromResult<LyricsPayload?>(null);
    }

}
