using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Lyrics.Models;
using MediaIsland.Services.Media;
using Xunit;

namespace MediaIsland.Tests;

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
        bool returnsPayload = true) : ILyricsProvider
    {
        public LyricsSourceId Id => id;

        public Task<IReadOnlyList<LyricsCandidate>> SearchAsync(
            MediaInfo media,
            LyricsSourceSettings settings,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<LyricsCandidate> candidates =
            [
                new LyricsCandidate(
                    id,
                    id.ToString(),
                    media.Title ?? string.Empty,
                    media.Artist ?? string.Empty,
                    media.AlbumTitle ?? string.Empty,
                    media.Duration,
                    150,
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
            IReadOnlyList<LyricsWord> words = payload.Format == LyricsFormat.Qrc
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
            preferWordSync: payload.Format == LyricsFormat.Qrc);
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
}
