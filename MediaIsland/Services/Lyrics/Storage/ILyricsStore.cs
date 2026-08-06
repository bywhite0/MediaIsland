namespace MediaIsland.Services.Lyrics.Storage;

/// <summary>
/// 歌词持久化存储。缓存与固定条目分目录存放，使「清空缓存」不会触碰固定条目。
/// </summary>
/// <remarks>
/// 所有方法都不得抛出：持久化是纯优化，其失败模式必须是「退化成没有缓存的行为」。
/// </remarks>
internal interface ILyricsStore
{
    /// <summary>读取固定条目。不做设置指纹校验——固定是用户的最终裁决，与搜索偏好无关。</summary>
    Task<StoredLyrics?> TryGetPinAsync(string key, CancellationToken cancellationToken);

    /// <summary>读取缓存条目。指纹不匹配时仍然返回，由调用方决定是否回落。</summary>
    Task<StoredLyrics?> TryGetCacheAsync(string key, CancellationToken cancellationToken);

    Task SavePinAsync(string key, StoredLyrics entry, CancellationToken cancellationToken);

    Task SaveCacheAsync(string key, StoredLyrics entry, CancellationToken cancellationToken);

    /// <summary>解除固定；条目不存在时静默返回 false。</summary>
    Task<bool> RemovePinAsync(string key, CancellationToken cancellationToken);

    /// <summary>清空全部缓存条目，固定条目不受影响。</summary>
    Task ClearCacheAsync(CancellationToken cancellationToken);

    /// <summary>刷新最后使用时间，供 LRU 淘汰排序。失败不影响调用方。</summary>
    Task TouchAsync(string key, DateTimeOffset nowUtc, CancellationToken cancellationToken);

    /// <summary>列出全部条目元信息，供设置页展示。</summary>
    Task<IReadOnlyList<LyricsStoreIndexEntry>> ListAsync(CancellationToken cancellationToken);
}
