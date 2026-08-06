using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MediaIsland.Services.Lyrics.Storage;

/// <summary>
/// 一曲一文件的歌词落盘实现。
/// </summary>
/// <remarks>
/// 一曲一文件而非单一大索引：天然并发安全（临时文件 + 原子重命名）、
/// 单条损坏只影响一首、无需把整库读进内存。
/// index.json 仅承载元信息，损坏或丢失都能从目录扫描重建。
/// </remarks>
internal sealed class LyricsFileStore : ILyricsStore
{
    private const string CacheFolderName = "cache";
    private const string PinFolderName = "pins";
    private const string IndexFileName = "index.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly string _root;
    private readonly string _cacheFolder;
    private readonly string _pinFolder;
    private readonly string _indexPath;
    private readonly ILogger<LyricsFileStore>? _logger;
    private readonly int _cacheCapacity;
    private readonly int _cacheTrimTarget;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _keyLocks = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _indexLock = new(1, 1);
    private readonly object _degradeSync = new();

    private Dictionary<string, LyricsStoreIndexEntry>? _index;
    private bool _isDegraded;

    public LyricsFileStore(
        string rootFolder,
        ILogger<LyricsFileStore>? logger = null,
        int cacheCapacity = 2000,
        int cacheTrimTarget = 1800)
    {
        _root = rootFolder;
        _cacheFolder = Path.Combine(rootFolder, CacheFolderName);
        _pinFolder = Path.Combine(rootFolder, PinFolderName);
        _indexPath = Path.Combine(rootFolder, IndexFileName);
        _logger = logger;
        _cacheCapacity = cacheCapacity;
        _cacheTrimTarget = Math.Min(cacheTrimTarget, cacheCapacity);
    }

    public Task<StoredLyrics?> TryGetPinAsync(string key, CancellationToken cancellationToken) =>
        TryReadAsync(_pinFolder, key, cancellationToken);

    public Task<StoredLyrics?> TryGetCacheAsync(string key, CancellationToken cancellationToken) =>
        TryReadAsync(_cacheFolder, key, cancellationToken);

    public Task SavePinAsync(string key, StoredLyrics entry, CancellationToken cancellationToken) =>
        SaveAsync(_pinFolder, key, entry, isPinned: true, cancellationToken);

    public Task SaveCacheAsync(string key, StoredLyrics entry, CancellationToken cancellationToken) =>
        SaveAsync(_cacheFolder, key, entry, isPinned: false, cancellationToken);

    public async Task<bool> RemovePinAsync(string key, CancellationToken cancellationToken)
    {
        if (IsDegraded || !TryGetEntryPath(_pinFolder, key, out var path))
        {
            return false;
        }

        try
        {
            var gate = GetKeyLock(key);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!File.Exists(path))
                {
                    // 文件已不在，但索引可能还留着条目——一并清掉，否则设置页会出现删不掉的幽灵行。
                    await RemoveFromIndexAsync(key, isPinned: true, cancellationToken).ConfigureAwait(false);
                    return false;
                }

                File.Delete(path);
                await RemoveFromIndexAsync(key, isPinned: true, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger?.LogWarning(ex, "[歌词] 解除固定失败：{Key}", key);
                return false;
            }
            finally
            {
                gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // 取消不抛出，退化为「未解除」。
            return false;
        }
    }

    public async Task ClearCacheAsync(CancellationToken cancellationToken)
    {
        if (IsDegraded)
        {
            return;
        }

        try
        {
            if (Directory.Exists(_cacheFolder))
            {
                Directory.Delete(_cacheFolder, recursive: true);
            }

            Directory.CreateDirectory(_cacheFolder);

            await _indexLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var index = await LoadIndexNoLockAsync(cancellationToken).ConfigureAwait(false);
                foreach (var key in index.Where(pair => !pair.Value.IsPinned).Select(pair => pair.Key).ToArray())
                {
                    index.Remove(key);
                }

                await WriteIndexNoLockAsync(index, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _indexLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // 取消不抛出，退化为 no-op。
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "[歌词] 清空歌词缓存失败。");
        }
    }

    public async Task TouchAsync(string key, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        if (IsDegraded)
        {
            return;
        }

        try
        {
            await _indexLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var index = await LoadIndexNoLockAsync(cancellationToken).ConfigureAwait(false);
                // 同一 key 的 pin 与 cache 是同一首歌的两份落盘，一并刷新。
                var touched = false;
                foreach (var isPinned in new[] { false, true })
                {
                    var indexKey = IndexKey(key, isPinned);
                    if (index.TryGetValue(indexKey, out var entry))
                    {
                        index[indexKey] = entry with { LastUsedAtUtc = nowUtc };
                        touched = true;
                    }
                }

                if (!touched)
                {
                    return;
                }

                await WriteIndexNoLockAsync(index, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger?.LogDebug(ex, "[歌词] 更新最后使用时间失败：{Key}", key);
            }
            finally
            {
                _indexLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // 取消不抛出，退化为 no-op。
        }
    }

    public async Task<IReadOnlyList<LyricsStoreIndexEntry>> ListAsync(CancellationToken cancellationToken)
    {
        if (IsDegraded)
        {
            return [];
        }

        try
        {
            await _indexLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var index = await LoadIndexNoLockAsync(cancellationToken).ConfigureAwait(false);
                return index.Values.ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger?.LogWarning(ex, "[歌词] 读取歌词索引失败。");
                return [];
            }
            finally
            {
                _indexLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // 取消不抛出，退化为空列表。
            return [];
        }
    }

    private bool IsDegraded
    {
        get
        {
            lock (_degradeSync)
            {
                return _isDegraded;
            }
        }
    }

    private async Task<StoredLyrics?> TryReadAsync(string folder, string key, CancellationToken cancellationToken)
    {
        if (IsDegraded || !TryGetEntryPath(folder, key, out var path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer
                .DeserializeAsync<StoredLyrics>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 取消不算损坏，退化为未命中，且不删除文件。
            return null;
        }
        catch (JsonException ex)
        {
            // 损坏条目当未命中，并顺手删除，避免每次播放都重复失败。
            _logger?.LogWarning(ex, "[歌词] 歌词条目已损坏，按未命中处理：{Path}", path);
            TryDelete(path);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "[歌词] 读取歌词条目失败：{Path}", path);
            return null;
        }
    }

    private async Task SaveAsync(
        string folder,
        string key,
        StoredLyrics entry,
        bool isPinned,
        CancellationToken cancellationToken)
    {
        if (!EnsureFolders() || !TryGetEntryPath(folder, key, out var path))
        {
            return;
        }

        try
        {
            var gate = GetKeyLock(key);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await WriteAtomicAsync(path, entry, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger?.LogWarning(ex, "[歌词] 写入歌词条目失败：{Path}", path);
                return;
            }
            finally
            {
                gate.Release();
            }

            await UpsertIndexAsync(key, entry, isPinned, cancellationToken).ConfigureAwait(false);
            if (!isPinned)
            {
                await TrimCacheAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 取消不抛出，退化为 no-op。写入被取消时最坏是这一条没落盘，缓存本就是纯优化。
        }
    }

    private async Task WriteAtomicAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        // 临时文件 + 原子重命名：进程中途退出也不会留下半截文件。
        var temporaryPath = path + ".tmp";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporaryPath, path, overwrite: true);
    }

    private async Task UpsertIndexAsync(
        string key,
        StoredLyrics entry,
        bool isPinned,
        CancellationToken cancellationToken)
    {
        await _indexLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = await LoadIndexNoLockAsync(cancellationToken).ConfigureAwait(false);
            index[IndexKey(key, isPinned)] = new LyricsStoreIndexEntry(
                key,
                entry.Title,
                entry.Artist,
                entry.Album,
                entry.SettingsFingerprint,
                entry.LastUsedAtUtc,
                isPinned,
                entry.Source.ToString());
            await WriteIndexNoLockAsync(index, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogDebug(ex, "[歌词] 更新歌词索引失败：{Key}", key);
        }
        finally
        {
            _indexLock.Release();
        }
    }

    private async Task RemoveFromIndexAsync(string key, bool isPinned, CancellationToken cancellationToken)
    {
        await _indexLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = await LoadIndexNoLockAsync(cancellationToken).ConfigureAwait(false);
            if (index.Remove(IndexKey(key, isPinned)))
            {
                await WriteIndexNoLockAsync(index, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogDebug(ex, "[歌词] 从索引移除条目失败：{Key}", key);
        }
        finally
        {
            _indexLock.Release();
        }
    }

    private async Task TrimCacheAsync(CancellationToken cancellationToken)
    {
        await _indexLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = await LoadIndexNoLockAsync(cancellationToken).ConfigureAwait(false);
            var cached = index.Values.Where(entry => !entry.IsPinned).ToArray();
            if (cached.Length <= _cacheCapacity)
            {
                return;
            }

            // 一次删到 trimTarget，避免每次写入都触发淘汰。
            var victims = cached
                .OrderBy(entry => entry.LastUsedAtUtc)
                .Take(cached.Length - _cacheTrimTarget)
                .ToArray();

            foreach (var victim in victims)
            {
                if (TryGetEntryPath(_cacheFolder, victim.Key, out var path))
                {
                    TryDelete(path);
                }

                // victim 恒为非 pin 项（上面已按 !IsPinned 筛过），索引键需拼复合前缀。
                index.Remove(IndexKey(victim.Key, isPinned: false));
            }

            await WriteIndexNoLockAsync(index, cancellationToken).ConfigureAwait(false);
            _logger?.LogInformation("[歌词] 歌词缓存已淘汰 {Count} 条最久未使用的条目。", victims.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "[歌词] 淘汰歌词缓存失败。");
        }
        finally
        {
            _indexLock.Release();
        }
    }

    private async Task<Dictionary<string, LyricsStoreIndexEntry>> LoadIndexNoLockAsync(
        CancellationToken cancellationToken)
    {
        if (_index is not null)
        {
            return _index;
        }

        if (File.Exists(_indexPath))
        {
            try
            {
                await using var stream = File.OpenRead(_indexPath);
                var entries = await JsonSerializer
                    .DeserializeAsync<List<LyricsStoreIndexEntry>>(stream, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                if (entries is not null)
                {
                    // index.json 在用户可见的配置目录里，手工编辑与同步工具都可能产出
                    // 元素为 null 或缺 Key 的合法 JSON，这类条目映射不到任何文件，直接跳过。
                    _index = entries
                        .Where(entry => entry is not null && !string.IsNullOrWhiteSpace(entry.Key))
                        .ToDictionary(entry => IndexKey(entry.Key, entry.IsPinned), StringComparer.Ordinal);
                    return _index;
                }
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or IOException
                or UnauthorizedAccessException)
            {
                _logger?.LogWarning(ex, "[歌词] 歌词索引损坏，将从目录重建。");
            }
        }

        _index = RebuildIndexFromDisk();
        return _index;
    }

    /// <summary>
    /// 索引只是加速结构，真值在两个目录里。损坏或丢失都重建，重建失败则以空索引启动
    /// ——此时缓存文件仍可按 key 直接命中，只是 LRU 与列表暂时看不到它们。
    /// </summary>
    private Dictionary<string, LyricsStoreIndexEntry> RebuildIndexFromDisk()
    {
        var rebuilt = new Dictionary<string, LyricsStoreIndexEntry>(StringComparer.Ordinal);
        Scan(_cacheFolder, isPinned: false);
        Scan(_pinFolder, isPinned: true);
        return rebuilt;

        void Scan(string folder, bool isPinned)
        {
            if (!Directory.Exists(folder))
            {
                return;
            }

            foreach (var path in Directory.EnumerateFiles(folder, "*.json"))
            {
                var key = Path.GetFileNameWithoutExtension(path);
                try
                {
                    using var stream = File.OpenRead(path);
                    var entry = JsonSerializer.Deserialize<StoredLyrics>(stream, JsonOptions);
                    if (entry is null)
                    {
                        continue;
                    }

                    rebuilt[IndexKey(key, isPinned)] = new LyricsStoreIndexEntry(
                        key,
                        entry.Title,
                        entry.Artist,
                        entry.Album,
                        entry.SettingsFingerprint,
                        entry.LastUsedAtUtc,
                        isPinned,
                        entry.Source.ToString());
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    _logger?.LogDebug(ex, "[歌词] 重建索引时跳过损坏条目：{Path}", path);
                }
            }
        }
    }

    private async Task WriteIndexNoLockAsync(
        Dictionary<string, LyricsStoreIndexEntry> index,
        CancellationToken cancellationToken)
    {
        _index = index;
        await WriteAtomicAsync(_indexPath, index.Values.ToList(), cancellationToken).ConfigureAwait(false);
    }

    private bool EnsureFolders()
    {
        if (IsDegraded)
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(_cacheFolder);
            Directory.CreateDirectory(_pinFolder);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 配置目录不可写：记一次警告后本进程降级为纯内存缓存，不再重试。
            lock (_degradeSync)
            {
                if (!_isDegraded)
                {
                    _isDegraded = true;
                    _logger?.LogWarning(ex, "[歌词] 歌词缓存目录不可用，本次运行将不使用持久化缓存：{Root}", _root);
                }
            }

            return false;
        }
    }

    /// <summary>
    /// 内存索引的复合键：同一首歌的 pin 与 cache 落在不同目录，是两份独立条目。
    /// 若只用曲目键寻址，固定一首已缓存的歌会让 cache 条目从索引消失——
    /// 既不计入缓存上限、也永不被 LRU 淘汰，取消固定后更会变成读得到却列不出的孤儿。
    /// 磁盘格式不含此前缀，仅在载入与重建时拼装。
    /// </summary>
    private static string IndexKey(string key, bool isPinned) => (isPinned ? "p:" : "c:") + key;

    private static bool TryGetEntryPath(string folder, string key, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(key) || key.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }

        path = Path.Combine(folder, key + ".json");
        return true;
    }

    private SemaphoreSlim GetKeyLock(string key) =>
        _keyLocks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogDebug(ex, "[歌词] 删除歌词条目失败：{Path}", path);
        }
    }
}
