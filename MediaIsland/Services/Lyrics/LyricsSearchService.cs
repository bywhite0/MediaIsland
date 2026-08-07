using System.Collections.Concurrent;
using MediaIsland.Helpers;
using MediaIsland.Services.Lyrics.Models;
using MediaIsland.Services.Lyrics.Providers;
using MediaIsland.Services.Lyrics.Storage;
using MediaIsland.Services.Media;
using Microsoft.Extensions.Logging;

namespace MediaIsland.Services.Lyrics;

/// <summary>
/// 多来源歌词搜索协调器，按用户设置顺序确定性执行。
/// </summary>
public sealed class LyricsSearchService
{
    private enum LyricsSelectionMode
    {
        Any,
        LineOnly,
        WordOnly
    }

    private readonly IReadOnlyList<ILyricsProvider> _providers;
    private readonly IReadOnlyList<ILyricsPayloadParser> _parsers;
    private readonly Func<LyricsSourceSettings> _settingsFactory;
    private readonly ISPlayerNextLyricsClient? _sPlayerNextLyricsClient;
    private readonly ILyricsStore? _store;
    private readonly ILogger<LyricsSearchService>? _logger;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CandidateCacheEntry> _candidateCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _searchSync = new();
    private string _settingsFingerprint = string.Empty;
    private LyricsSearchResult? _currentResult;
    private string? _currentMediaTitle;
    private string? _currentMediaArtist;
    private long _searchVersion;
    private InflightSearch? _inflightSearch;

    public LyricsSearchResult? CurrentResult => Volatile.Read(ref _currentResult);

    public event EventHandler<LyricsSearchResultChangedEventArgs>? CurrentResultChanged;

    public event EventHandler<LyricsSearchResultChangedEventArgs>? CandidateApplied;

    public LyricsSearchResult? GetCurrentResultFor(MediaInfo? info)
    {
        var result = CurrentResult;
        return result != null &&
               info != null &&
               string.Equals(info.Title, Volatile.Read(ref _currentMediaTitle), StringComparison.Ordinal) &&
               string.Equals(info.Artist, Volatile.Read(ref _currentMediaArtist), StringComparison.Ordinal)
            ? result
            : null;
    }

    public LyricsSearchService(
        IEnumerable<ILyricsProvider> providers,
        IEnumerable<ILyricsPayloadParser> parsers,
        Func<LyricsSourceSettings> settingsFactory,
        ILogger<LyricsSearchService>? logger = null,
        ISPlayerNextLyricsClient? sPlayerNextLyricsClient = null)
        : this(providers, parsers, settingsFactory, logger, sPlayerNextLyricsClient, store: null)
    {
    }

    /// <summary>
    /// 带持久化存储的构造函数。<see cref="ILyricsStore"/> 是 internal，
    /// 故本重载不能公开——公开构造函数的参数类型不得低于其自身可访问性。
    /// </summary>
    internal LyricsSearchService(
        IEnumerable<ILyricsProvider> providers,
        IEnumerable<ILyricsPayloadParser> parsers,
        Func<LyricsSourceSettings> settingsFactory,
        ILogger<LyricsSearchService>? logger,
        ISPlayerNextLyricsClient? sPlayerNextLyricsClient,
        ILyricsStore? store)
    {
        _providers = providers.ToArray();
        _parsers = parsers.ToArray();
        _settingsFactory = settingsFactory;
        _logger = logger;
        _sPlayerNextLyricsClient = sPlayerNextLyricsClient;
        _store = store;
    }

    public async Task<LyricsSearchResult?> SearchAsync(
        MediaInfo info,
        CancellationToken cancellationToken = default,
        bool allowProviderSearch = true)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(info.Title))
        {
            var emptyVersion = BeginExclusiveSearch(info);
            PublishCurrentResult(null, emptyVersion);
            return null;
        }

        var settings = LyricsSourceSettings.Normalize(_settingsFactory().Clone());
        EnsureCacheFingerprint(settings);

        var cacheKey = BuildCacheKey(info, allowProviderSearch);
        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            if (cached.ExpiresAt > DateTimeOffset.UtcNow)
            {
                var cachedVersion = BeginExclusiveSearch(info);
                PublishCurrentResult(cached.Result, cachedVersion);
                return cached.Result;
            }

            _cache.TryRemove(cacheKey, out _);
        }

        var searchTask = GetOrStartSearchAsync(info, settings, cacheKey, allowProviderSearch);
        try
        {
            return await searchTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
    }

    private Task<LyricsSearchResult?> GetOrStartSearchAsync(
        MediaInfo info,
        LyricsSourceSettings settings,
        string cacheKey,
        bool allowProviderSearch)
    {
        lock (_searchSync)
        {
            if (_inflightSearch is { } inflight &&
                string.Equals(inflight.CacheKey, cacheKey, StringComparison.OrdinalIgnoreCase) &&
                !inflight.Task.IsCompleted)
            {
                return inflight.Task;
            }

            CancelInflightSearch_NoLock();

            var searchVersion = Interlocked.Increment(ref _searchVersion);
            Interlocked.Exchange(ref _currentMediaTitle, info.Title);
            Interlocked.Exchange(ref _currentMediaArtist, info.Artist);
            PublishCurrentResult(null, searchVersion);

            var cts = new CancellationTokenSource();
            var searchTask = ExecuteSearchAsync(
                info,
                settings,
                cacheKey,
                searchVersion,
                allowProviderSearch,
                cts.Token);
            _inflightSearch = new InflightSearch(cacheKey, searchTask, cts);
            _ = searchTask.ContinueWith(
                task =>
                {
                    lock (_searchSync)
                    {
                        if (ReferenceEquals(_inflightSearch?.Task, task))
                        {
                            _inflightSearch.Cts.Dispose();
                            _inflightSearch = null;
                        }
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            return searchTask;
        }
    }

    private async Task<LyricsSearchResult?> ExecuteSearchAsync(
        MediaInfo info,
        LyricsSourceSettings settings,
        string cacheKey,
        long searchVersion,
        bool allowProviderSearch,
        CancellationToken cancellationToken)
    {
        try
        {
            // 实时来源优先：SPlayer-Next 直连给出的是播放器此刻正在放的那份歌词，
            // 权威性高于本机对这首歌的历史判断（pin 与缓存）。
            if (_sPlayerNextLyricsClient != null && SPlayerNextMediaSource.Matches(info.SourceApp))
            {
                var direct = await _sPlayerNextLyricsClient
                    .TryFetchAsync(info, settings, cancellationToken)
                    .ConfigureAwait(false);
                if (direct != null)
                {
                    // 只写 L1：该歌词跟随 SPlayer-Next 的曲库，落盘后会在换源时变成静默错数据。
                    Cache(cacheKey, direct);
                    PublishCurrentResult(direct, searchVersion);
                    return direct;
                }

                if (!allowProviderSearch)
                {
                    _logger?.LogInformation(
                        "[歌词] SPlayer-Next 外部 API 无可用歌词，且该播放源未启用歌词搜索：{Title} - {Artist}",
                        info.Title,
                        info.Artist);
                    return null;
                }

                _logger?.LogInformation(
                    "[歌词] SPlayer-Next 外部 API 未返回可用歌词，回退到在线搜索：{Title} - {Artist}",
                    info.Title,
                    info.Artist);
            }

            // 来源级门禁：关闭歌词搜索意味着「这个来源不要本机解析的歌词」，
            // pin 与落盘缓存同样是本机解析的产物，必须一并跳过。
            // 若把 pin 放在门禁之前，用户为 VLC 关掉歌词后播放已 pin 曲目的 MV 会突然冒出歌词。
            if (!allowProviderSearch)
            {
                _logger?.LogInformation("[歌词] 已禁用歌词搜索，跳过本机歌词解析。");
                return null;
            }

            var trackKey = BuildTrackKey(info);
            var fingerprint = ComputeSettingsFingerprint(settings);

            var pinned = await TryLoadStoredAsync(trackKey, isPin: true, cancellationToken).ConfigureAwait(false);
            if (pinned != null)
            {
                var pinnedResult = await MaterializeAsync(pinned, info, cancellationToken).ConfigureAwait(false);
                if (pinnedResult != null)
                {
                    _logger?.LogInformation("[歌词] 使用已固定的歌词：{Title} - {Artist}", info.Title, info.Artist);
                    Cache(cacheKey, pinnedResult);
                    PublishCurrentResult(pinnedResult, searchVersion);
                    return pinnedResult;
                }
            }

            var cached = await TryLoadStoredAsync(trackKey, isPin: false, cancellationToken).ConfigureAwait(false);
            LyricsSearchResult? staleFallback = null;
            if (cached != null)
            {
                var cachedResult = await MaterializeAsync(cached, info, cancellationToken).ConfigureAwait(false);
                if (cachedResult != null)
                {
                    if (string.Equals(cached.SettingsFingerprint, fingerprint, StringComparison.Ordinal))
                    {
                        _logger?.LogInformation("[歌词] 命中本地歌词缓存：{Title} - {Artist}", info.Title, info.Artist);
                        Cache(cacheKey, cachedResult);
                        PublishCurrentResult(cachedResult, searchVersion);
                        TouchStoreInBackground(trackKey);
                        return cachedResult;
                    }

                    // 指纹不匹配只说明「重搜可能更合适」，不代表旧歌词无效。
                    // 先重搜，失败再回落——界面突然变空比歌词不合最新偏好糟糕得多。
                    staleFallback = cachedResult;
                }
            }

            var orderedProviders = settings.Sources
                .Where(source => source.IsEnabled)
                .Select(source => _providers.FirstOrDefault(provider => provider.Id == source.Id))
                .Where(provider => provider != null)
                .Cast<ILyricsProvider>()
                .ToArray();

            if (orderedProviders.Length == 0)
            {
                _logger?.LogInformation("[歌词] 没有启用的歌词来源。");
                return PublishFallback(staleFallback, cacheKey, searchVersion);
            }

            var result = await SearchOnceAsync(orderedProviders, info, settings, cancellationToken);
            if (result != null)
            {
                Cache(cacheKey, result);
                PublishCurrentResult(result, searchVersion);
                SaveToStoreInBackground(trackKey, result, info, fingerprint);
                return result;
            }

            _logger?.LogInformation("[歌词] 未找到歌词：{Title} - {Artist}", info.Title, info.Artist);
            return PublishFallback(staleFallback, cacheKey, searchVersion);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[歌词] 搜索歌词时发生错误。");
            PublishCurrentResult(null, searchVersion);
            return null;
        }
    }

    private LyricsSearchResult? PublishFallback(
        LyricsSearchResult? fallback,
        string cacheKey,
        long searchVersion)
    {
        if (fallback == null)
        {
            return null;
        }

        _logger?.LogInformation("[歌词] 重新搜索未果，回落到设置变更前的缓存歌词。");
        Cache(cacheKey, fallback);
        PublishCurrentResult(fallback, searchVersion);
        return fallback;
    }

    /// <summary>持久化是纯优化：任何读取失败都当作未命中，绝不影响歌词显示。</summary>
    private async Task<StoredLyrics?> TryLoadStoredAsync(
        string trackKey,
        bool isPin,
        CancellationToken cancellationToken)
    {
        if (_store == null)
        {
            return null;
        }

        try
        {
            return isPin
                ? await _store.TryGetPinAsync(trackKey, cancellationToken).ConfigureAwait(false)
                : await _store.TryGetCacheAsync(trackKey, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[歌词] 读取本地歌词条目失败，按未命中处理。");
            return null;
        }
    }

    /// <summary>把落盘的原始 payload 交给现有解析器还原成结果。</summary>
    private async Task<LyricsSearchResult?> MaterializeAsync(
        StoredLyrics stored,
        MediaInfo info,
        CancellationToken cancellationToken)
    {
        var payload = stored.ToPayload();
        var parser = _parsers.FirstOrDefault(item => item.CanParse(payload.Format));
        if (parser == null)
        {
            _logger?.LogWarning("[歌词] 没有适用于格式 {Format} 的解析器，跳过本地条目。", payload.Format);
            return null;
        }

        LyricsDocument document;
        try
        {
            document = await parser.ParseAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[歌词] 解析本地歌词条目失败。");
            return null;
        }

        // 此处刻意不调用 IsInstrumentalPlaceholder：在线路径挡占位歌词是为了让搜索继续尝试下一个源，
        // 而落盘条目没有「下一个源」可试。pin 的占位歌词是用户拿着文件明确选定的结果，
        // 挡掉等于替用户否决其选择，且界面无从显示否决原因。占位歌词也进不了缓存分支——
        // 在线路径已在写入前滤除，故缓存里不存在这种条目。
        if (document.Lines.Count == 0)
        {
            return null;
        }

        return new LyricsSearchResult(
            document,
            document.ProviderItemId,
            stored.Title ?? info.Title ?? string.Empty,
            stored.Artist ?? info.Artist ?? string.Empty,
            TimeSpan.FromMilliseconds(stored.DurationMs),
            null,
            document.Source);
    }

    /// <summary>
    /// 落盘 fire-and-forget：写入失败只记日志，绝不阻塞或影响歌词显示。
    /// 只有通用在线 provider 的结果才落盘——SPlayer-Next 跟随播放器曲库、
    /// External 是别的机器的当前状态、LocalFile 本就不经由搜索路径产生。
    /// </summary>
    private void SaveToStoreInBackground(
        string trackKey,
        LyricsSearchResult result,
        MediaInfo info,
        string fingerprint)
    {
        if (_store == null || !IsCacheableSource(result.Source) || result.Payload == null)
        {
            return;
        }

        var entry = StoredLyrics.FromPayload(
            result.Payload,
            info.Title,
            info.Artist,
            info.AlbumTitle,
            info.Duration,
            fingerprint,
            DateTimeOffset.UtcNow);

        _ = Task.Run(async () =>
        {
            try
            {
                await _store.SaveCacheAsync(trackKey, entry, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[歌词] 写入本地歌词缓存失败。");
            }
        });
    }

    private void TouchStoreInBackground(string trackKey)
    {
        if (_store == null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _store.TouchAsync(trackKey, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "[歌词] 更新歌词缓存使用时间失败。");
            }
        });
    }

    internal static bool IsCacheableSource(LyricsSourceId source) =>
        source is LyricsSourceId.Netease
            or LyricsSourceId.QqMusic
            or LyricsSourceId.Kugou
            or LyricsSourceId.AmllTtml;

    private long BeginExclusiveSearch(MediaInfo info)
    {
        lock (_searchSync)
        {
            CancelInflightSearch_NoLock();
            var searchVersion = Interlocked.Increment(ref _searchVersion);
            Interlocked.Exchange(ref _currentMediaTitle, info.Title);
            Interlocked.Exchange(ref _currentMediaArtist, info.Artist);
            return searchVersion;
        }
    }

    private void CancelInflightSearch_NoLock()
    {
        if (_inflightSearch is not { } inflight)
        {
            return;
        }

        try
        {
            inflight.Cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        inflight.Cts.Dispose();
        _inflightSearch = null;
    }

    public async Task<IReadOnlyList<LyricsCandidate>> SearchCandidatesAsync(
        MediaInfo info,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(info.Title))
        {
            return [];
        }

        var settings = LyricsSourceSettings.Normalize(_settingsFactory().Clone());
        EnsureCacheFingerprint(settings);
        var orderedProviders = settings.Sources
            .Where(source => source.IsEnabled)
            .Select(source => _providers.FirstOrDefault(provider => provider.Id == source.Id))
            .Where(provider => provider != null)
            .Cast<ILyricsProvider>()
            .ToArray();
        if (orderedProviders.Length == 0)
        {
            return [];
        }

        var candidatesByProvider = await Task.WhenAll(orderedProviders.Select(provider =>
            SearchProviderCandidatesAsync(
                provider,
                info,
                settings,
                cancellationToken,
                includeBelowMinimumScore: true)));
        return candidatesByProvider
            .SelectMany(candidates => candidates)
            .OrderByDescending(candidate => candidate.Score)
            .ToArray();
    }

    public async Task<LyricsSearchResult?> ApplyCandidateAsync(
        MediaInfo info,
        LyricsCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(info.Title))
        {
            return null;
        }

        var settings = LyricsSourceSettings.Normalize(_settingsFactory().Clone());
        if (!settings.IsSourceEnabled(candidate.Source))
        {
            return null;
        }

        var provider = _providers.FirstOrDefault(item => item.Id == candidate.Source);
        if (provider == null)
        {
            return null;
        }

        EnsureCacheFingerprint(settings);
        var searchVersion = BeginExclusiveSearch(info);

        var result = await TrySelectFromProviderAsync(
            provider,
            [candidate],
            settings,
            LyricsSelectionMode.Any,
            cancellationToken);
        if (result == null)
        {
            return null;
        }

        if (searchVersion != Volatile.Read(ref _searchVersion))
        {
            return null;
        }

        Cache(BuildCacheKey(info), result);
        PublishCurrentResult(result, searchVersion);
        if (!ReferenceEquals(CurrentResult, result))
        {
            return null;
        }

        if (searchVersion == Volatile.Read(ref _searchVersion))
        {
            PublishCandidateApplied(result);
        }

        return result;
    }

    private string? _lastPinError;

    /// <summary>最近一次固定操作的失败原因，供设置页展示；成功时为 null。</summary>
    public string? LastPinError => Volatile.Read(ref _lastPinError);

    /// <summary>
    /// 把当前生效的歌词固定下来。用户手动改歌词通常是因为自动匹配错了，
    /// 因此固定条目不设过期、不参与设置指纹校验，只能由用户自己解除。
    /// </summary>
    public async Task<LyricsSearchResult?> PinCurrentResultAsync(
        MediaInfo info,
        CancellationToken cancellationToken = default)
    {
        Volatile.Write(ref _lastPinError, null);
        if (_store == null || string.IsNullOrWhiteSpace(info.Title))
        {
            Volatile.Write(ref _lastPinError, "当前没有可固定的歌词。");
            return null;
        }

        var current = GetCurrentResultFor(info);
        if (current?.Payload == null)
        {
            Volatile.Write(ref _lastPinError, "当前歌词没有可保存的原始内容，无法固定。");
            return null;
        }

        var entry = StoredLyrics.FromPayload(
            current.Payload,
            info.Title,
            info.Artist,
            info.AlbumTitle,
            info.Duration,
            ComputeSettingsFingerprint(_settingsFactory()),
            DateTimeOffset.UtcNow);

        return await SavePinAsync(info, entry, current, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 从本地文件导入并固定。文件内容复制进条目、不引用外部路径，
    /// 用户随后删除或移动源文件都不影响歌词可用。
    /// </summary>
    public async Task<LyricsSearchResult?> PinFromFileAsync(
        MediaInfo info,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        Volatile.Write(ref _lastPinError, null);
        if (_store == null || string.IsNullOrWhiteSpace(info.Title))
        {
            Volatile.Write(ref _lastPinError, "请先播放曲目，再导入歌词文件。");
            return null;
        }

        var read = LocalLyricsFileReader.Read(filePath);
        if (read.Payload == null)
        {
            Volatile.Write(ref _lastPinError, read.ErrorMessage ?? "无法读取该歌词文件。");
            return null;
        }

        var entry = StoredLyrics.FromPayload(
            read.Payload,
            info.Title,
            info.Artist,
            info.AlbumTitle,
            info.Duration,
            ComputeSettingsFingerprint(_settingsFactory()),
            DateTimeOffset.UtcNow);

        // 解析出 0 行就拒绝写入：静默接受空歌词会让用户以为导入成功了。
        // 校验放在这里而非 LocalLyricsFileReader，是因为只有这一层能拿到
        // 依赖注入的完整解析器链（含静态代码调用不到的 TTML 解析器）。
        var materialized = await MaterializeAsync(entry, info, cancellationToken).ConfigureAwait(false);
        if (materialized == null)
        {
            Volatile.Write(ref _lastPinError, "该歌词文件没有解析出任何歌词行，未导入。");
            return null;
        }

        return await SavePinAsync(info, entry, materialized, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> UnpinAsync(MediaInfo info, CancellationToken cancellationToken = default)
    {
        if (_store == null || string.IsNullOrWhiteSpace(info.Title))
        {
            return false;
        }

        var trackKey = BuildTrackKey(info);
        try
        {
            var removed = await _store.RemovePinAsync(trackKey, cancellationToken).ConfigureAwait(false);
            if (removed)
            {
                InvalidateMemoryCacheFor(trackKey);
            }

            return removed;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[歌词] 解除固定失败。");
            return false;
        }
    }

    public async Task<bool> HasPinAsync(MediaInfo info, CancellationToken cancellationToken = default) =>
        await GetPinAsync(info, cancellationToken).ConfigureAwait(false) != null;

    /// <summary>
    /// 取当前曲目的固定条目。设置页要展示「固定于哪天、来自哪个源」，
    /// 只返回布尔值会逼调用方拿当前时间充数，显示出与事实不符的日期。
    /// </summary>
    internal async Task<StoredLyrics?> GetPinAsync(
        MediaInfo info,
        CancellationToken cancellationToken = default)
    {
        if (_store == null || string.IsNullOrWhiteSpace(info.Title))
        {
            return null;
        }

        return await TryLoadStoredAsync(BuildTrackKey(info), isPin: true, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task ClearPersistentCacheAsync(CancellationToken cancellationToken = default)
    {
        if (_store == null)
        {
            return;
        }

        try
        {
            await _store.ClearCacheAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[歌词] 清空歌词缓存失败。");
        }

        // 清缓存必须一并清 L1，否则界面还会继续显示刚被删掉的条目。
        _cache.Clear();
        _candidateCache.Clear();
    }

    private async Task<LyricsSearchResult?> SavePinAsync(
        MediaInfo info,
        StoredLyrics entry,
        LyricsSearchResult result,
        CancellationToken cancellationToken)
    {
        var trackKey = BuildTrackKey(info);
        try
        {
            await _store!.SavePinAsync(trackKey, entry, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[歌词] 保存固定歌词失败。");
            Volatile.Write(ref _lastPinError, "保存固定歌词失败，请检查配置目录是否可写。");
            return null;
        }

        InvalidateMemoryCacheFor(trackKey);
        var searchVersion = BeginExclusiveSearch(info);
        PublishCurrentResult(result, searchVersion);
        PublishCandidateApplied(result);
        return result;
    }

    /// <summary>
    /// L1 排在 pin 之前只是为了省一次磁盘读，前提是二者永不冲突。
    /// 因此固定状态一变，该曲目的全部 L1 条目（各上下文位组合）必须立刻失效。
    /// </summary>
    private void InvalidateMemoryCacheFor(string trackKey)
    {
        foreach (var key in _cache.Keys.Where(key => key.StartsWith(trackKey, StringComparison.Ordinal)).ToArray())
        {
            _cache.TryRemove(key, out _);
        }

        foreach (var key in _candidateCache.Keys
                     .Where(key => key.StartsWith(trackKey, StringComparison.Ordinal))
                     .ToArray())
        {
            _candidateCache.TryRemove(key, out _);
        }
    }

    private async Task<LyricsSearchResult?> SearchOnceAsync(
        IReadOnlyList<ILyricsProvider> orderedProviders,
        MediaInfo info,
        LyricsSourceSettings settings,
        CancellationToken cancellationToken)
    {
        var providerCandidates = new List<(ILyricsProvider Provider, IReadOnlyList<LyricsCandidate> Candidates)>();
        foreach (var provider in orderedProviders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidates = await SearchProviderCandidatesAsync(provider, info, settings, cancellationToken);
            providerCandidates.Add((provider, candidates));

            if (!settings.PreferWordSync(provider.Id))
            {
                continue;
            }

            var result = await TrySelectFromProviderAsync(
                provider,
                candidates,
                settings,
                LyricsSelectionMode.WordOnly,
                cancellationToken);
            if (result != null)
            {
                return result;
            }
        }

        foreach (var entry in providerCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await TrySelectFromProviderAsync(
                entry.Provider,
                entry.Candidates,
                settings,
                LyricsSelectionMode.LineOnly,
                cancellationToken);
            if (result != null)
            {
                return result;
            }
        }

        foreach (var entry in providerCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await TrySelectFromProviderAsync(
                entry.Provider,
                entry.Candidates,
                settings,
                LyricsSelectionMode.Any,
                cancellationToken);
            if (result != null)
            {
                return result;
            }
        }

        return null;
    }

    public void InvalidateCache()
    {
        _cache.Clear();
        _candidateCache.Clear();
        _settingsFingerprint = string.Empty;
    }

    private async Task<IReadOnlyList<LyricsCandidate>> SearchProviderCandidatesAsync(
        ILyricsProvider provider,
        MediaInfo info,
        LyricsSourceSettings settings,
        CancellationToken cancellationToken,
        bool includeBelowMinimumScore = false)
    {
        var candidateCacheKey = BuildCandidateCacheKey(info, provider.Id);
        if (TryGetCachedCandidates(candidateCacheKey, out var cachedCandidates))
        {
            return FilterCandidates(cachedCandidates, info, includeBelowMinimumScore);
        }

        try
        {
            var candidates = await provider.SearchAsync(info, settings, cancellationToken);
            var orderedCandidates = candidates
                .OrderByDescending(candidate => candidate.Score)
                .ToArray();
            CacheCandidates(candidateCacheKey, orderedCandidates);
            return FilterCandidates(orderedCandidates, info, includeBelowMinimumScore);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger?.LogWarning("[歌词:{Provider}] 搜索超时。", provider.Id);
            return [];
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[歌词:{Provider}] 搜索失败。", provider.Id);
            return [];
        }
    }

    private static IReadOnlyList<LyricsCandidate> FilterCandidates(
        IReadOnlyList<LyricsCandidate> candidates,
        MediaInfo info,
        bool includeBelowMinimumScore)
    {
        if (includeBelowMinimumScore)
        {
            return candidates;
        }

        return candidates
            .Where(candidate => candidate.Score >= LyricsCandidateScorer.MinimumScore(info))
            .ToArray();
    }

    private bool TryGetCachedCandidates(string key, out IReadOnlyList<LyricsCandidate> candidates)
    {
        if (_candidateCache.TryGetValue(key, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
        {
            candidates = cached.Candidates;
            return true;
        }

        if (cached is not null)
        {
            _candidateCache.TryRemove(key, out _);
        }

        candidates = [];
        return false;
    }

    private void CacheCandidates(string key, IReadOnlyList<LyricsCandidate> candidates)
    {
        _candidateCache[key] = new CandidateCacheEntry(candidates, DateTimeOffset.UtcNow.AddMinutes(30));
    }

    private async Task<LyricsSearchResult?> TrySelectFromProviderAsync(
        ILyricsProvider provider,
        IReadOnlyList<LyricsCandidate> candidates,
        LyricsSourceSettings settings,
        LyricsSelectionMode selectionMode,
        CancellationToken cancellationToken)
    {
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (selectionMode == LyricsSelectionMode.WordOnly && !candidate.SupportsWordSync)
            {
                continue;
            }

            LyricsPayload? payload;
            try
            {
                payload = await provider.FetchAsync(candidate, settings, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger?.LogWarning(
                    "[歌词:{Provider}] 获取歌词超时，ID={Id}",
                    provider.Id,
                    candidate.ProviderItemId);
                continue;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[歌词:{Provider}] 获取歌词失败，ID={Id}", provider.Id, candidate.ProviderItemId);
                continue;
            }

            if (payload == null || string.IsNullOrWhiteSpace(payload.Content))
            {
                continue;
            }

            var isWordPayload = payload.Format is LyricsFormat.Qrc or LyricsFormat.Krc or LyricsFormat.Ttml;
            if (selectionMode == LyricsSelectionMode.WordOnly && !isWordPayload)
            {
                continue;
            }

            var parser = _parsers.FirstOrDefault(item => item.CanParse(payload.Format));
            if (parser == null)
            {
                _logger?.LogWarning("[歌词:{Provider}] 没有适用于格式 {Format} 的解析器。", provider.Id, payload.Format);
                continue;
            }

            LyricsDocument document;
            try
            {
                document = await parser.ParseAsync(payload, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[歌词:{Provider}] 解析歌词失败，ID={Id}", provider.Id, candidate.ProviderItemId);
                continue;
            }

            if (document.Lines.Count == 0 || IsInstrumentalPlaceholder(document))
            {
                continue;
            }

            if (selectionMode == LyricsSelectionMode.WordOnly && document.SyncMode != LyricsSyncMode.Word)
            {
                continue;
            }

            if (selectionMode == LyricsSelectionMode.LineOnly && document.SyncMode != LyricsSyncMode.Line)
            {
                continue;
            }

            if (selectionMode != LyricsSelectionMode.LineOnly &&
                !settings.PreferWordSync(provider.Id) &&
                document.SyncMode == LyricsSyncMode.Word)
            {
                document = LyricsDocumentNormalizer.Create(
                    document.Lines,
                    document.Metadata,
                    document.Source,
                    document.ProviderItemId,
                    document.Format,
                    preferWordSync: false);
            }

            _logger?.LogInformation(
                "[歌词] 已选择 {Source}/{Format}，同步方式={Sync}，ID={Id}，评分={Score}，标题={Title}",
                document.Source,
                document.Format,
                document.SyncMode,
                document.ProviderItemId,
                candidate.Score,
                candidate.Title);

            return new LyricsSearchResult(
                document,
                document.ProviderItemId,
                candidate.Title,
                candidate.Artist,
                candidate.Duration,
                candidate.Score,
                document.Source)
            {
                Payload = payload
            };
        }

        return null;
    }

    /// <summary>
    /// 各歌词源对纯音乐会返回占位歌词而非空结果。若当作有效歌词接受，搜索会就此停止，
    /// 不再尝试其他可能真正收录了歌词的来源，用户看到的就是「搜不到歌词」。
    /// </summary>
    private static readonly string[] InstrumentalPlaceholders =
    [
        "纯音乐，请欣赏",
        "纯音乐,请欣赏",
        "没有填词的纯音乐",
        "此歌曲为没有填词的纯音乐"
    ];

    internal static bool IsInstrumentalPlaceholder(LyricsDocument document)
    {
        return document.Lines.Any(line =>
            !string.IsNullOrWhiteSpace(line.Text) &&
            InstrumentalPlaceholders.Any(placeholder =>
                line.Text.Contains(placeholder, StringComparison.Ordinal)));
    }

    /// <summary>
    /// 计算搜索偏好指纹，用于判断落盘条目是否产自当前设置。
    /// </summary>
    /// <remarks>
    /// 先规范化再计算：服务始终在规范化后的设置上搜索，而调用方（如落盘时的元信息组装）
    /// 手上可能是尚未补齐默认源的原始设置。若两者算出不同指纹，整库缓存会被静默判为过期。
    /// 用 <c>Clone</c> 是因为 <see cref="LyricsSourceSettings.Normalize"/> 会就地修改入参。
    /// </remarks>
    internal static string ComputeSettingsFingerprint(LyricsSourceSettings settings)
    {
        var normalized = LyricsSourceSettings.Normalize(settings.Clone());
        return string.Join(
            "|",
            normalized.Sources.Select(source => $"{source.Id}:{source.IsEnabled}:{source.UseWordSyncedLyrics}"))
            + "|" + normalized.AmllApiBaseUrl
            + "|" + normalized.SPlayerNextApiBaseUrl;
    }

    private void EnsureCacheFingerprint(LyricsSourceSettings settings)
    {
        var fingerprint = ComputeSettingsFingerprint(settings);
        if (!string.Equals(fingerprint, _settingsFingerprint, StringComparison.Ordinal))
        {
            _cache.Clear();
            _candidateCache.Clear();
            _settingsFingerprint = fingerprint;
        }
    }

    /// <summary>
    /// L2（落盘）键：纯曲目标识，不含播放器与时长，使同一首歌跨播放器命中同一条目。
    /// </summary>
    internal static string BuildTrackKey(MediaInfo info) =>
        LyricsTrackKey.Compute(info.Title, info.Artist, info.AlbumTitle);

    /// <summary>
    /// L1（内存）键：曲目标识 + 解析上下文。
    /// </summary>
    /// <remarks>
    /// 两个上下文位都是必需的：去掉 <paramref name="allowProviderSearch"/> 会造成泄漏
    /// ——用户关闭某来源的歌词搜索后，30 分钟内 L1 仍会返回歌词，因为设置指纹
    /// 只覆盖 Lyrics 设置、不覆盖 MediaSourceList；去掉 isSPlayerNext 则会把直连结果
    /// 串给普通播放源。时长不进键：播放器上报的时长有抖动，分桶边界会让缓存永久不命中。
    /// </remarks>
    internal static string BuildCacheKey(MediaInfo info, bool allowProviderSearch = true) =>
        string.Join(
            '\u001f',
            BuildTrackKey(info),
            SPlayerNextMediaSource.Matches(info.SourceApp) ? "1" : "0",
            allowProviderSearch ? "1" : "0");

    internal static string BuildCandidateCacheKey(MediaInfo info, LyricsSourceId providerId) =>
        string.Join(
            '\u001f',
            BuildTrackKey(info),
            SPlayerNextMediaSource.Matches(info.SourceApp) ? "1" : "0",
            providerId);

    private void Cache(string key, LyricsSearchResult result)
    {
        _cache[key] = new CacheEntry(result, DateTimeOffset.UtcNow.AddMinutes(30));
    }

    private void PublishCurrentResult(LyricsSearchResult? result, long searchVersion)
    {
        if (searchVersion != Volatile.Read(ref _searchVersion))
        {
            return;
        }

        var previous = Interlocked.Exchange(ref _currentResult, result);
        var handlers = CurrentResultChanged;
        if (Equals(previous, result) || handlers == null)
        {
            return;
        }

        handlers(this, new LyricsSearchResultChangedEventArgs(result));
    }

    private void PublishCandidateApplied(LyricsSearchResult result)
    {
        CandidateApplied?.Invoke(this, new LyricsSearchResultChangedEventArgs(result));
    }

    private sealed record CacheEntry(LyricsSearchResult Result, DateTimeOffset ExpiresAt);

    private sealed record CandidateCacheEntry(IReadOnlyList<LyricsCandidate> Candidates, DateTimeOffset ExpiresAt);

    private sealed record InflightSearch(
        string CacheKey,
        Task<LyricsSearchResult?> Task,
        CancellationTokenSource Cts);
}

/// <param name="Source">
/// 本机处理该歌词所用的通道。外部注入一律为 <see cref="LyricsSourceId.External"/>——
/// 这不只是标签，组件据此决定是否直接应用（见 LyricsComponent 的 OnExternalLyricsChanged）。
/// </param>
/// <param name="OriginSource">
/// 歌词最初的来源。跨实例转发时，上游可能是 QQ 音乐等真实来源，但对本机而言通道仍是
/// External；此字段仅用于界面显示"这份歌词来自哪里"，不参与任何路由判断。
/// null 表示与 <paramref name="Source"/> 相同。
/// </param>
public sealed record LyricsSearchResult(
    LyricsDocument Document,
    string Id,
    string Title,
    string Artist,
    TimeSpan Duration,
    int? Score,
    LyricsSourceId Source,
    LyricsSourceId? OriginSource = null)
{
    /// <summary>
    /// 产生该结果的原始载荷，仅用于落盘缓存；外部注入与本地条目还原时为 null。
    /// </summary>
    /// <remarks>
    /// 注意：record 的相等性是按字段生成的，本属性的后备字段同样参与 <c>Equals</c>——
    /// 声明在体内而非主构造函数并不能把它排除在外。这对去重无实际影响：
    /// <see cref="LyricsDocument.Lines"/> 是 <c>List</c>，两次独立解析本就不相等，
    /// 而 L1 命中返回的是同一实例，引用相等先短路。若将来需要真正排除，
    /// 必须手写 <c>Equals</c> 与 <c>GetHashCode</c>。
    /// </remarks>
    [System.Text.Json.Serialization.JsonIgnore]
    public LyricsPayload? Payload { get; init; }
}

public sealed class LyricsSearchResultChangedEventArgs(LyricsSearchResult? result) : EventArgs
{
    public LyricsSearchResult? Result { get; } = result;
}
