using MediaIsland.Services.Lyrics.Models;
using MediaIsland.Services.Lyrics.Storage;
using Xunit;

namespace MediaIsland.Tests.Lyrics;

public class LyricsFileStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "MediaIslandLyricsStoreTests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private static readonly DateTimeOffset Now = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    private LyricsFileStore CreateStore(int capacity = 2000, int trimTarget = 1800) =>
        new(_root, logger: null, cacheCapacity: capacity, cacheTrimTarget: trimTarget);

    private static StoredLyrics CreateEntry(
        string content = "payload",
        string fingerprint = "fp-1",
        DateTimeOffset? lastUsed = null) =>
        StoredLyrics.FromPayload(
            new LyricsPayload(
                LyricsFormat.Lrc,
                content,
                LyricsSourceId.Netease,
                "song-1",
                new LyricsMetadata("Lemon", "米津玄師", "Lemon", TimeSpan.FromSeconds(256))),
            "Lemon",
            "米津玄師",
            "Lemon",
            TimeSpan.FromSeconds(256),
            fingerprint,
            lastUsed ?? Now);

    [Fact]
    public async Task SaveCache_ThenTryGetCache_ReturnsEntry()
    {
        var store = CreateStore();

        await store.SaveCacheAsync("key-1", CreateEntry(), CancellationToken.None);
        var loaded = await store.TryGetCacheAsync("key-1", CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal("payload", loaded!.Content);
    }

    [Fact]
    public async Task TryGetCache_ReturnsNull_ForUnknownKey()
    {
        var store = CreateStore();

        Assert.Null(await store.TryGetCacheAsync("missing", CancellationToken.None));
    }

    /// <summary>缓存与固定分目录，互不干扰。</summary>
    [Fact]
    public async Task PinAndCache_AreIndependent()
    {
        var store = CreateStore();

        await store.SaveCacheAsync("key-1", CreateEntry("cache-content"), CancellationToken.None);
        await store.SavePinAsync("key-1", CreateEntry("pin-content"), CancellationToken.None);

        var cache = await store.TryGetCacheAsync("key-1", CancellationToken.None);
        var pin = await store.TryGetPinAsync("key-1", CancellationToken.None);

        Assert.Equal("cache-content", cache!.Content);
        Assert.Equal("pin-content", pin!.Content);
    }

    [Fact]
    public async Task ClearCache_KeepsPins()
    {
        var store = CreateStore();
        await store.SaveCacheAsync("key-1", CreateEntry("cache-content"), CancellationToken.None);
        await store.SavePinAsync("key-2", CreateEntry("pin-content"), CancellationToken.None);

        await store.ClearCacheAsync(CancellationToken.None);

        Assert.Null(await store.TryGetCacheAsync("key-1", CancellationToken.None));
        Assert.NotNull(await store.TryGetPinAsync("key-2", CancellationToken.None));
    }

    [Fact]
    public async Task RemovePin_DeletesEntry_AndReportsWhetherItExisted()
    {
        var store = CreateStore();
        await store.SavePinAsync("key-1", CreateEntry(), CancellationToken.None);

        Assert.True(await store.RemovePinAsync("key-1", CancellationToken.None));
        Assert.Null(await store.TryGetPinAsync("key-1", CancellationToken.None));
        Assert.False(await store.RemovePinAsync("key-1", CancellationToken.None));
    }

    /// <summary>损坏文件当未命中处理，并顺手删除，避免反复失败。</summary>
    [Fact]
    public async Task TryGetCache_CorruptFile_ReturnsNullAndDeletesIt()
    {
        var store = CreateStore();
        await store.SaveCacheAsync("key-1", CreateEntry(), CancellationToken.None);
        var path = Path.Combine(_root, "cache", "key-1.json");
        await File.WriteAllTextAsync(path, "{ this is not valid json");

        Assert.Null(await store.TryGetCacheAsync("key-1", CancellationToken.None));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task List_ReturnsBothPinnedAndCachedEntries()
    {
        var store = CreateStore();
        await store.SaveCacheAsync("key-1", CreateEntry(), CancellationToken.None);
        await store.SavePinAsync("key-2", CreateEntry(), CancellationToken.None);

        var entries = await store.ListAsync(CancellationToken.None);

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, entry => entry.Key == "key-1" && !entry.IsPinned);
        Assert.Contains(entries, entry => entry.Key == "key-2" && entry.IsPinned);
    }

    /// <summary>索引丢失后可从目录重建，缓存不会因此失效。</summary>
    [Fact]
    public async Task MissingIndex_IsRebuiltFromDirectories()
    {
        var store = CreateStore();
        await store.SaveCacheAsync("key-1", CreateEntry(), CancellationToken.None);
        await store.SavePinAsync("key-2", CreateEntry(), CancellationToken.None);

        File.Delete(Path.Combine(_root, "index.json"));
        var reopened = CreateStore();

        var entries = await reopened.ListAsync(CancellationToken.None);

        Assert.Equal(2, entries.Count);
        Assert.NotNull(await reopened.TryGetCacheAsync("key-1", CancellationToken.None));
        Assert.NotNull(await reopened.TryGetPinAsync("key-2", CancellationToken.None));
    }

    [Fact]
    public async Task CorruptIndex_FallsBackToDirectoryScan()
    {
        var store = CreateStore();
        await store.SaveCacheAsync("key-1", CreateEntry(), CancellationToken.None);

        await File.WriteAllTextAsync(Path.Combine(_root, "index.json"), "not json at all");
        var reopened = CreateStore();

        Assert.NotNull(await reopened.TryGetCacheAsync("key-1", CancellationToken.None));
        Assert.Single(await reopened.ListAsync(CancellationToken.None));
    }

    /// <summary>
    /// 索引是合法 JSON 但语义非法（重复 Key）时，ToDictionary 抛的是 ArgumentException——
    /// 它不属于 JSON/IO 异常族，若不接住会持续逃逸：_index 保持 null，每次调用重新读盘重新抛，
    /// 直到用户手工删掉 index.json。index.json 位于用户可见的配置目录，手工编辑与多机同步都会产出这种文件。
    /// </summary>
    [Fact]
    public async Task SemanticallyInvalidIndex_FallsBackToDirectoryScan()
    {
        var store = CreateStore();
        await store.SaveCacheAsync("key-1", CreateEntry(), CancellationToken.None);

        var duplicated = """
        [
          {"Key":"key-1","Title":"A","Artist":"B","Album":"C","SettingsFingerprint":"fp","LastUsedAtUtc":"2026-08-06T12:00:00+00:00","IsPinned":false,"Source":"Netease"},
          {"Key":"key-1","Title":"A","Artist":"B","Album":"C","SettingsFingerprint":"fp","LastUsedAtUtc":"2026-08-06T12:00:00+00:00","IsPinned":false,"Source":"Netease"}
        ]
        """;
        await File.WriteAllTextAsync(Path.Combine(_root, "index.json"), duplicated);
        var reopened = CreateStore();

        // 六个碰索引的方法都不得抛出，且应从目录重建后正常工作。
        Assert.Null(await Record.ExceptionAsync(() => reopened.ListAsync(CancellationToken.None)));
        Assert.NotNull(await reopened.TryGetCacheAsync("key-1", CancellationToken.None));
        Assert.Single(await reopened.ListAsync(CancellationToken.None));
    }

    /// <summary>
    /// pin 与 cache 对同一首歌必然同 key（L2 键就是曲目标识），索引须按复合键区分：
    /// 否则固定一首已缓存的歌会让 cache 条目从索引消失——不计入上限、永不被 LRU 选中，
    /// 解除固定后更会变成「读得到却列不出」的孤儿。
    /// </summary>
    [Fact]
    public async Task PinAndCache_WithSameKey_CoexistInIndex()
    {
        var store = CreateStore();

        await store.SaveCacheAsync("same", CreateEntry("cache-content"), CancellationToken.None);
        await store.SavePinAsync("same", CreateEntry("pin-content"), CancellationToken.None);

        var entries = await store.ListAsync(CancellationToken.None);
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, entry => entry.Key == "same" && entry.IsPinned);
        Assert.Contains(entries, entry => entry.Key == "same" && !entry.IsPinned);

        // 解除固定后，cache 条目必须仍在索引里，而不是被一并抹掉。
        Assert.True(await store.RemovePinAsync("same", CancellationToken.None));
        var remaining = Assert.Single(await store.ListAsync(CancellationToken.None));
        Assert.False(remaining.IsPinned);
        Assert.NotNull(await store.TryGetCacheAsync("same", CancellationToken.None));
    }

    /// <summary>LRU 只淘汰缓存，固定条目不计入也不被删。</summary>
    [Fact]
    public async Task Lru_TrimsOldestCacheEntries_AndNeverTouchesPins()
    {
        var store = CreateStore(capacity: 4, trimTarget: 2);
        await store.SavePinAsync("pinned", CreateEntry("pin"), CancellationToken.None);

        for (var i = 0; i < 5; i++)
        {
            await store.SaveCacheAsync(
                $"key-{i}",
                CreateEntry($"content-{i}", lastUsed: Now.AddMinutes(i)),
                CancellationToken.None);
        }

        var cached = (await store.ListAsync(CancellationToken.None))
            .Where(entry => !entry.IsPinned)
            .ToArray();

        Assert.True(cached.Length <= 2, $"缓存条目应被裁剪到 2 个以内，实际 {cached.Length} 个。");
        Assert.Null(await store.TryGetCacheAsync("key-0", CancellationToken.None));
        Assert.NotNull(await store.TryGetCacheAsync("key-4", CancellationToken.None));
        Assert.NotNull(await store.TryGetPinAsync("pinned", CancellationToken.None));
    }

    [Fact]
    public async Task Touch_UpdatesLastUsedAt()
    {
        var store = CreateStore();
        await store.SaveCacheAsync("key-1", CreateEntry(), CancellationToken.None);

        await store.TouchAsync("key-1", Now.AddHours(3), CancellationToken.None);

        var entry = Assert.Single(await store.ListAsync(CancellationToken.None));
        Assert.Equal(Now.AddHours(3), entry.LastUsedAtUtc);
    }

    /// <summary>同一 key 并发写必须串行化，不得写出半截文件。</summary>
    [Fact]
    public async Task ConcurrentWrites_ToSameKey_DoNotCorruptTheFile()
    {
        var store = CreateStore();

        await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            store.SaveCacheAsync("key-1", CreateEntry($"content-{i}"), CancellationToken.None)));

        var loaded = await store.TryGetCacheAsync("key-1", CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.StartsWith("content-", loaded!.Content, StringComparison.Ordinal);
    }

    /// <summary>根目录不可用时降级为无缓存，不得抛出。</summary>
    [Fact]
    public async Task UnusableRoot_DegradesSilently()
    {
        var filePath = Path.Combine(_root, "not-a-directory");
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(filePath, "occupied");
        var store = new LyricsFileStore(filePath, logger: null);

        await store.SaveCacheAsync("key-1", CreateEntry(), CancellationToken.None);

        Assert.Null(await store.TryGetCacheAsync("key-1", CancellationToken.None));
        Assert.Empty(await store.ListAsync(CancellationToken.None));
    }

    /// <summary>
    /// 取消同样不得抛出：缓存是纯优化，切歌触发的取消不应变成调用方的故障。
    /// </summary>
    [Fact]
    public async Task AllOperations_WithCancelledToken_DegradeSilently()
    {
        var store = CreateStore();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var entry = CreateEntry();

        Assert.Null(await Record.ExceptionAsync(() => store.SaveCacheAsync("k", entry, cts.Token)));
        Assert.Null(await Record.ExceptionAsync(() => store.SavePinAsync("k", entry, cts.Token)));
        Assert.Null(await Record.ExceptionAsync(() => store.TryGetCacheAsync("k", cts.Token)));
        Assert.Null(await Record.ExceptionAsync(() => store.TryGetPinAsync("k", cts.Token)));
        Assert.Null(await Record.ExceptionAsync(() => store.RemovePinAsync("k", cts.Token)));
        Assert.Null(await Record.ExceptionAsync(() => store.ClearCacheAsync(cts.Token)));
        Assert.Null(await Record.ExceptionAsync(() => store.TouchAsync("k", DateTimeOffset.UtcNow, cts.Token)));
        Assert.Null(await Record.ExceptionAsync(() => store.ListAsync(cts.Token)));
    }
}
