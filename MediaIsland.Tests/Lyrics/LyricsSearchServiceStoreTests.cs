using System.Collections.Concurrent;
using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Lyrics.Models;
using MediaIsland.Services.Lyrics.Storage;
using MediaIsland.Services.Media;
using Xunit;

namespace MediaIsland.Tests.Lyrics;

public class LyricsSearchServiceStoreTests
{
    [Fact]
    public async Task SearchAsync_PinWins_OverCacheAndProviders()
    {
        var store = new FakeStore();
        var media = CreateMedia();
        store.Pins[LyricsSearchService.BuildTrackKey(media)] = CreateStored("pinned", LyricsSourceId.LocalFile);
        store.Caches[LyricsSearchService.BuildTrackKey(media)] = CreateStored("cached", LyricsSourceId.Netease);
        var provider = new CountingProvider(LyricsSourceId.QqMusic);
        var service = CreateService(store, provider);

        var result = await service.SearchAsync(media);

        Assert.NotNull(result);
        Assert.Equal(LyricsSourceId.LocalFile, result!.Source);
        Assert.Equal(0, provider.SearchCallCount);
    }

    [Fact]
    public async Task SearchAsync_CacheHit_SkipsProvidersEntirely()
    {
        var store = new FakeStore();
        var media = CreateMedia();
        store.Caches[LyricsSearchService.BuildTrackKey(media)] = CreateStored("cached", LyricsSourceId.Netease);
        var provider = new CountingProvider(LyricsSourceId.QqMusic);
        var service = CreateService(store, provider);

        var result = await service.SearchAsync(media);

        Assert.NotNull(result);
        Assert.Equal(LyricsSourceId.Netease, result!.Source);
        Assert.Equal(0, provider.SearchCallCount);
    }

    /// <summary>
    /// 来源级门禁：关闭歌词搜索的播放源，pin 与缓存都不得生效。
    /// 回归防护——用户为 Edge/VLC 关掉歌词后播放已 pin 曲目的 MV，不应突然冒出歌词。
    /// </summary>
    [Fact]
    public async Task SearchAsync_SearchDisabled_SkipsPinAndCache()
    {
        var store = new FakeStore();
        var media = CreateMedia("vlc.exe");
        store.Pins[LyricsSearchService.BuildTrackKey(media)] = CreateStored("pinned", LyricsSourceId.LocalFile);
        store.Caches[LyricsSearchService.BuildTrackKey(media)] = CreateStored("cached", LyricsSourceId.Netease);
        var service = CreateService(store, new CountingProvider(LyricsSourceId.QqMusic));

        var result = await service.SearchAsync(media, allowProviderSearch: false);

        Assert.Null(result);
    }

    [Fact]
    public async Task SearchAsync_ProviderResult_IsWrittenToCache()
    {
        var store = new FakeStore();
        var media = CreateMedia();
        var service = CreateService(store, new CountingProvider(LyricsSourceId.QqMusic, returnsPayload: true));

        var result = await service.SearchAsync(media);

        Assert.NotNull(result);
        await store.WaitForWriteAsync();
        Assert.True(store.Caches.ContainsKey(LyricsSearchService.BuildTrackKey(media)));
    }

    [Fact]
    public async Task SearchAsync_NoResult_WritesNothing()
    {
        var store = new FakeStore();
        var service = CreateService(store, new CountingProvider(LyricsSourceId.QqMusic, returnsPayload: false));

        var result = await service.SearchAsync(CreateMedia());

        Assert.Null(result);
        Assert.Empty(store.Caches);
    }

    /// <summary>指纹不匹配先重搜；重搜失败则回落旧条目，界面不能因此变空。</summary>
    [Fact]
    public async Task SearchAsync_StaleFingerprint_FallsBackToOldEntry_WhenResearchFails()
    {
        var store = new FakeStore();
        var media = CreateMedia();
        store.Caches[LyricsSearchService.BuildTrackKey(media)] =
            CreateStored("cached", LyricsSourceId.Netease) with { SettingsFingerprint = "stale-fingerprint" };
        var provider = new CountingProvider(LyricsSourceId.QqMusic, returnsPayload: false);
        var service = CreateService(store, provider);

        var result = await service.SearchAsync(media);

        Assert.NotNull(result);
        Assert.Equal(LyricsSourceId.Netease, result!.Source);
        Assert.Equal(1, provider.SearchCallCount);
    }

    [Fact]
    public async Task SearchAsync_StaleFingerprint_PrefersFreshResult_WhenResearchSucceeds()
    {
        var store = new FakeStore();
        var media = CreateMedia();
        store.Caches[LyricsSearchService.BuildTrackKey(media)] =
            CreateStored("cached", LyricsSourceId.Netease) with { SettingsFingerprint = "stale-fingerprint" };
        var service = CreateService(store, new CountingProvider(LyricsSourceId.QqMusic, returnsPayload: true));

        var result = await service.SearchAsync(media);

        Assert.NotNull(result);
        Assert.Equal(LyricsSourceId.QqMusic, result!.Source);
    }

    /// <summary>pin 不参与指纹校验——改歌词源开关不该把用户固定的歌词冲掉。</summary>
    [Fact]
    public async Task SearchAsync_PinIgnoresFingerprint()
    {
        var store = new FakeStore();
        var media = CreateMedia();
        store.Pins[LyricsSearchService.BuildTrackKey(media)] =
            CreateStored("pinned", LyricsSourceId.LocalFile) with { SettingsFingerprint = "totally-different" };
        var provider = new CountingProvider(LyricsSourceId.QqMusic);
        var service = CreateService(store, provider);

        var result = await service.SearchAsync(media);

        Assert.NotNull(result);
        Assert.Equal(LyricsSourceId.LocalFile, result!.Source);
        Assert.Equal(0, provider.SearchCallCount);
    }

    [Fact]
    public async Task SearchAsync_CacheHit_TouchesLastUsedAt()
    {
        var store = new FakeStore();
        var media = CreateMedia();
        store.Caches[LyricsSearchService.BuildTrackKey(media)] = CreateStored("cached", LyricsSourceId.Netease);
        var service = CreateService(store, new CountingProvider(LyricsSourceId.QqMusic));

        await service.SearchAsync(media);
        await store.WaitForWriteAsync();

        Assert.Contains(LyricsSearchService.BuildTrackKey(media), store.TouchedKeys);
    }

    [Fact]
    public async Task SearchAsync_StoreFailure_DegradesToOnlineSearch()
    {
        var service = CreateService(new ThrowingStore(), new CountingProvider(LyricsSourceId.QqMusic, returnsPayload: true));

        var result = await service.SearchAsync(CreateMedia());

        Assert.NotNull(result);
        Assert.Equal(LyricsSourceId.QqMusic, result!.Source);
    }

    private static LyricsSearchService CreateService(ILyricsStore store, params ILyricsProvider[] providers) =>
        new(
            providers,
            [new PassThroughParser()],
            () => new LyricsSourceSettings
            {
                Sources =
                [
                    new LyricsSourceEntry { Id = LyricsSourceId.QqMusic, IsEnabled = true, UseWordSyncedLyrics = false }
                ]
            },
            logger: null,
            sPlayerNextLyricsClient: null,
            store: store);

    private static MediaInfo CreateMedia(string sourceApp = "Spotify.exe") =>
        new(
            sourceApp,
            "Lemon",
            "米津玄師",
            "Lemon",
            TimeSpan.Zero,
            TimeSpan.FromSeconds(256),
            new MediaPlaybackInfo(MediaPlaybackState.Playing),
            null,
            null);

    private static StoredLyrics CreateStored(string content, LyricsSourceId source) =>
        StoredLyrics.FromPayload(
            new LyricsPayload(
                LyricsFormat.Lrc,
                $"[00:01.00]{content}",
                source,
                content,
                new LyricsMetadata("Lemon", "米津玄師", "Lemon", TimeSpan.FromSeconds(256))),
            "Lemon",
            "米津玄師",
            "Lemon",
            TimeSpan.FromSeconds(256),
            LyricsSearchService.ComputeSettingsFingerprint(new LyricsSourceSettings
            {
                Sources =
                [
                    new LyricsSourceEntry { Id = LyricsSourceId.QqMusic, IsEnabled = true, UseWordSyncedLyrics = false }
                ]
            }),
            DateTimeOffset.UtcNow);

    private sealed class FakeStore : ILyricsStore
    {
        private readonly TaskCompletionSource _written = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentDictionary<string, StoredLyrics> Pins { get; } = new(StringComparer.Ordinal);

        public ConcurrentDictionary<string, StoredLyrics> Caches { get; } = new(StringComparer.Ordinal);

        public ConcurrentBag<string> TouchedKeys { get; } = [];

        public Task<StoredLyrics?> TryGetPinAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(Pins.GetValueOrDefault(key));

        public Task<StoredLyrics?> TryGetCacheAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(Caches.GetValueOrDefault(key));

        public Task SavePinAsync(string key, StoredLyrics entry, CancellationToken cancellationToken)
        {
            Pins[key] = entry;
            _written.TrySetResult();
            return Task.CompletedTask;
        }

        public Task SaveCacheAsync(string key, StoredLyrics entry, CancellationToken cancellationToken)
        {
            Caches[key] = entry;
            _written.TrySetResult();
            return Task.CompletedTask;
        }

        public Task<bool> RemovePinAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(Pins.TryRemove(key, out _));

        public Task ClearCacheAsync(CancellationToken cancellationToken)
        {
            Caches.Clear();
            return Task.CompletedTask;
        }

        public Task TouchAsync(string key, DateTimeOffset nowUtc, CancellationToken cancellationToken)
        {
            TouchedKeys.Add(key);
            _written.TrySetResult();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<LyricsStoreIndexEntry>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LyricsStoreIndexEntry>>([]);

        /// <summary>落盘是 fire-and-forget，测试需要等它真正发生。</summary>
        public Task WaitForWriteAsync() => _written.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class ThrowingStore : ILyricsStore
    {
        public Task<StoredLyrics?> TryGetPinAsync(string key, CancellationToken cancellationToken) =>
            throw new IOException("store is broken");

        public Task<StoredLyrics?> TryGetCacheAsync(string key, CancellationToken cancellationToken) =>
            throw new IOException("store is broken");

        public Task SavePinAsync(string key, StoredLyrics entry, CancellationToken cancellationToken) =>
            throw new IOException("store is broken");

        public Task SaveCacheAsync(string key, StoredLyrics entry, CancellationToken cancellationToken) =>
            throw new IOException("store is broken");

        public Task<bool> RemovePinAsync(string key, CancellationToken cancellationToken) =>
            throw new IOException("store is broken");

        public Task ClearCacheAsync(CancellationToken cancellationToken) =>
            throw new IOException("store is broken");

        public Task TouchAsync(string key, DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
            throw new IOException("store is broken");

        public Task<IReadOnlyList<LyricsStoreIndexEntry>> ListAsync(CancellationToken cancellationToken) =>
            throw new IOException("store is broken");
    }

    private sealed class CountingProvider(
        LyricsSourceId id,
        bool returnsPayload = true) : ILyricsProvider
    {
        public int SearchCallCount { get; private set; }

        public LyricsSourceId Id => id;

        public Task<IReadOnlyList<LyricsCandidate>> SearchAsync(
            MediaInfo media,
            LyricsSourceSettings settings,
            CancellationToken cancellationToken)
        {
            SearchCallCount++;
            IReadOnlyList<LyricsCandidate> candidates =
            [
                new LyricsCandidate(
                    id,
                    id.ToString(),
                    media.Title ?? string.Empty,
                    media.Artist ?? string.Empty,
                    media.AlbumTitle ?? string.Empty,
                    media.Duration,
                    200,
                    SupportsWordSync: false)
            ];
            return Task.FromResult(candidates);
        }

        public Task<LyricsPayload?> FetchAsync(
            LyricsCandidate candidate,
            LyricsSourceSettings settings,
            CancellationToken cancellationToken) =>
            Task.FromResult(returnsPayload
                ? new LyricsPayload(
                    LyricsFormat.Lrc,
                    "[00:01.00]fresh",
                    id,
                    candidate.ProviderItemId,
                    new LyricsMetadata(candidate.Title, candidate.Artist, candidate.Album, candidate.Duration))
                : null);
    }

    private sealed class PassThroughParser : ILyricsPayloadParser
    {
        public bool CanParse(LyricsFormat format) => true;

        public ValueTask<LyricsDocument> ParseAsync(
            LyricsPayload payload,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(LyricsDocumentNormalizer.Create(
                [new LyricsLine(TimeSpan.Zero, TimeSpan.FromSeconds(1), payload.Content, [])],
                payload.Metadata,
                payload.Source,
                payload.ProviderItemId,
                payload.Format,
                preferWordSync: false));
    }
}
