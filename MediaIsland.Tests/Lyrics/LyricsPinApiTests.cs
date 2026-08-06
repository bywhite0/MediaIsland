using System.Collections.Concurrent;
using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Lyrics.Models;
using MediaIsland.Services.Lyrics.Storage;
using MediaIsland.Services.Media;
using Xunit;

namespace MediaIsland.Tests.Lyrics;

public class LyricsPinApiTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "MediaIslandPinApiTests",
        Guid.NewGuid().ToString("N"));

    public LyricsPinApiTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task PinCurrentResultAsync_StoresTheCurrentSearchResult()
    {
        var store = new RecordingStore();
        var service = CreateService(store);
        var media = CreateMedia();
        await service.SearchAsync(media);

        var pinned = await service.PinCurrentResultAsync(media);

        Assert.NotNull(pinned);
        Assert.True(store.Pins.ContainsKey(LyricsSearchService.BuildTrackKey(media)));
    }

    [Fact]
    public async Task PinCurrentResultAsync_WithoutResult_ReturnsNull()
    {
        var service = CreateService(new RecordingStore());

        Assert.Null(await service.PinCurrentResultAsync(CreateMedia()));
    }

    [Fact]
    public async Task PinFromFileAsync_ImportsLocalFile()
    {
        var store = new RecordingStore();
        var service = CreateService(store);
        var media = CreateMedia();
        var path = Path.Combine(_folder, "song.lrc");
        await File.WriteAllTextAsync(path, "[00:01.00]本地歌词");

        var pinned = await service.PinFromFileAsync(media, path);

        Assert.NotNull(pinned);
        Assert.Equal(LyricsSourceId.LocalFile, pinned!.Source);
        Assert.True(store.Pins.ContainsKey(LyricsSearchService.BuildTrackKey(media)));
        Assert.Null(service.LastPinError);
    }

    /// <summary>解析出 0 行必须报错且不写 pin——静默接受空歌词会让用户以为成功了。</summary>
    [Fact]
    public async Task PinFromFileAsync_EmptyFile_ReportsErrorAndWritesNothing()
    {
        var store = new RecordingStore();
        var service = CreateService(store);
        var path = Path.Combine(_folder, "empty.lrc");
        await File.WriteAllTextAsync(path, "");

        var pinned = await service.PinFromFileAsync(CreateMedia(), path);

        Assert.Null(pinned);
        Assert.NotNull(service.LastPinError);
        Assert.Empty(store.Pins);
    }

    [Fact]
    public async Task PinFromFileAsync_MetadataOnlyFile_ReportsErrorAndWritesNothing()
    {
        var store = new RecordingStore();
        var service = CreateService(store);
        var path = Path.Combine(_folder, "meta.lrc");
        await File.WriteAllTextAsync(path, "[ar:Artist]\n[ti:Title]");

        var pinned = await service.PinFromFileAsync(CreateMedia(), path);

        Assert.Null(pinned);
        Assert.NotNull(service.LastPinError);
        Assert.Empty(store.Pins);
    }

    [Fact]
    public async Task UnpinAsync_RemovesThePin()
    {
        var store = new RecordingStore();
        var service = CreateService(store);
        var media = CreateMedia();
        var path = Path.Combine(_folder, "song.lrc");
        await File.WriteAllTextAsync(path, "[00:01.00]本地歌词");
        await service.PinFromFileAsync(media, path);

        Assert.True(await service.UnpinAsync(media));
        Assert.False(await service.HasPinAsync(media));
        Assert.False(await service.UnpinAsync(media));
    }

    /// <summary>写入或解除 pin 必须同步清掉该 key 的 L1 条目，否则两层会打架。</summary>
    [Fact]
    public async Task PinAndUnpin_InvalidateTheMemoryCache()
    {
        var store = new RecordingStore();
        var provider = new SingleShotProvider();
        var service = CreateService(store, provider);
        var media = CreateMedia();
        await service.SearchAsync(media);
        Assert.Equal(1, provider.SearchCallCount);

        var path = Path.Combine(_folder, "song.lrc");
        await File.WriteAllTextAsync(path, "[00:01.00]本地歌词");
        await service.PinFromFileAsync(media, path);

        var afterPin = await service.SearchAsync(media);
        Assert.Equal(LyricsSourceId.LocalFile, afterPin!.Source);

        await service.UnpinAsync(media);
        var afterUnpin = await service.SearchAsync(media);
        Assert.Equal(LyricsSourceId.QqMusic, afterUnpin!.Source);
    }

    [Fact]
    public async Task ClearPersistentCacheAsync_ClearsCacheButKeepsPins()
    {
        var store = new RecordingStore();
        var service = CreateService(store);
        var media = CreateMedia();
        var path = Path.Combine(_folder, "song.lrc");
        await File.WriteAllTextAsync(path, "[00:01.00]本地歌词");
        await service.PinFromFileAsync(media, path);
        store.Caches["some-key"] = CreateStored();

        await service.ClearPersistentCacheAsync();

        Assert.Empty(store.Caches);
        Assert.Single(store.Pins);
    }

    private static LyricsSearchService CreateService(ILyricsStore store, ILyricsProvider? provider = null) =>
        new(
            [provider ?? new SingleShotProvider()],
            [new ManagedLyricsPayloadParserStub()],
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

    private static MediaInfo CreateMedia() =>
        new(
            "Spotify.exe",
            "Lemon",
            "米津玄師",
            "Lemon",
            TimeSpan.Zero,
            TimeSpan.FromSeconds(256),
            new MediaPlaybackInfo(MediaPlaybackState.Playing),
            null,
            null);

    private static StoredLyrics CreateStored() =>
        StoredLyrics.FromPayload(
            new LyricsPayload(
                LyricsFormat.Lrc,
                "[00:01.00]cached",
                LyricsSourceId.Netease,
                "cached",
                new LyricsMetadata("Lemon", "米津玄師", "Lemon", TimeSpan.FromSeconds(256))),
            "Lemon",
            "米津玄師",
            "Lemon",
            TimeSpan.FromSeconds(256),
            "fp",
            DateTimeOffset.UtcNow);

    private sealed class RecordingStore : ILyricsStore
    {
        public ConcurrentDictionary<string, StoredLyrics> Pins { get; } = new(StringComparer.Ordinal);

        public ConcurrentDictionary<string, StoredLyrics> Caches { get; } = new(StringComparer.Ordinal);

        public Task<StoredLyrics?> TryGetPinAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(Pins.GetValueOrDefault(key));

        public Task<StoredLyrics?> TryGetCacheAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(Caches.GetValueOrDefault(key));

        public Task SavePinAsync(string key, StoredLyrics entry, CancellationToken cancellationToken)
        {
            Pins[key] = entry;
            return Task.CompletedTask;
        }

        public Task SaveCacheAsync(string key, StoredLyrics entry, CancellationToken cancellationToken)
        {
            Caches[key] = entry;
            return Task.CompletedTask;
        }

        public Task<bool> RemovePinAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(Pins.TryRemove(key, out _));

        public Task ClearCacheAsync(CancellationToken cancellationToken)
        {
            Caches.Clear();
            return Task.CompletedTask;
        }

        public Task TouchAsync(string key, DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<LyricsStoreIndexEntry>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LyricsStoreIndexEntry>>([]);
    }

    private sealed class SingleShotProvider : ILyricsProvider
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
                    LyricsSourceId.QqMusic,
                    "qq-1",
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
            Task.FromResult<LyricsPayload?>(new LyricsPayload(
                LyricsFormat.Lrc,
                "[00:01.00]online",
                LyricsSourceId.QqMusic,
                candidate.ProviderItemId,
                new LyricsMetadata(candidate.Title, candidate.Artist, candidate.Album, candidate.Duration)));
    }

    /// <summary>用真实的 LRC 解析逻辑，才能验证「解析出 0 行则拒绝写入」。</summary>
    private sealed class ManagedLyricsPayloadParserStub : ILyricsPayloadParser
    {
        private readonly Services.Lyrics.Parsers.ManagedLyricsPayloadParser _inner = new();

        public bool CanParse(LyricsFormat format) => true;

        public ValueTask<LyricsDocument> ParseAsync(
            LyricsPayload payload,
            CancellationToken cancellationToken) =>
            _inner.ParseAsync(payload, cancellationToken);
    }
}
