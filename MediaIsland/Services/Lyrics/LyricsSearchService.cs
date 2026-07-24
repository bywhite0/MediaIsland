using System.Collections.Concurrent;
using MediaIsland.Services.Lyrics.Models;
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
        ILogger<LyricsSearchService>? logger = null)
    {
        _providers = providers.ToArray();
        _parsers = parsers.ToArray();
        _settingsFactory = settingsFactory;
        _logger = logger;
    }

    public async Task<LyricsSearchResult?> SearchAsync(MediaInfo info, CancellationToken cancellationToken = default)
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

        var cacheKey = BuildCacheKey(info, settings);
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

        var searchTask = GetOrStartSearchAsync(info, settings, cacheKey);
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
        string cacheKey)
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
            var searchTask = ExecuteSearchAsync(info, settings, cacheKey, searchVersion, cts.Token);
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
        CancellationToken cancellationToken)
    {
        try
        {
            var orderedProviders = settings.Sources
                .Where(source => source.IsEnabled)
                .Select(source => _providers.FirstOrDefault(provider => provider.Id == source.Id))
                .Where(provider => provider != null)
                .Cast<ILyricsProvider>()
                .ToArray();

            if (orderedProviders.Length == 0)
            {
                _logger?.LogInformation("[歌词] 没有启用的歌词来源。");
                return null;
            }

            var result = await SearchOnceAsync(orderedProviders, info, settings, cancellationToken);
            if (result != null)
            {
                Cache(cacheKey, result);
                PublishCurrentResult(result, searchVersion);
                return result;
            }

            _logger?.LogInformation("[歌词] 未找到歌词：{Title} - {Artist}", info.Title, info.Artist);
            return null;
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

        Cache(BuildCacheKey(info, settings), result);
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
        var candidateCacheKey = BuildCandidateCacheKey(info, settings, provider.Id);
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

            if (document.Lines.Count == 0)
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
                document.Source);
        }

        return null;
    }

    private void EnsureCacheFingerprint(LyricsSourceSettings settings)
    {
        var fingerprint = string.Join(
            "|",
            settings.Sources.Select(source => $"{source.Id}:{source.IsEnabled}:{source.UseWordSyncedLyrics}"))
            + "|" + settings.AmllApiBaseUrl;
        if (!string.Equals(fingerprint, _settingsFingerprint, StringComparison.Ordinal))
        {
            _cache.Clear();
            _candidateCache.Clear();
            _settingsFingerprint = fingerprint;
        }
    }

    private static string BuildCacheKey(MediaInfo info, LyricsSourceSettings settings)
    {
        var durationBucket = GetDurationBucket(info);
        var sourceFlags = string.Join(
            ",",
            settings.Sources.Select(source => $"{source.Id}:{(source.IsEnabled ? 1 : 0)}:{(source.UseWordSyncedLyrics ? 1 : 0)}"));
        return string.Join(
            "\u001f",
            LyricsTextNormalizer.NormalizeComparableText(info.Title),
            LyricsTextNormalizer.NormalizeComparableText(info.Artist),
            LyricsTextNormalizer.NormalizeComparableText(info.AlbumTitle),
            durationBucket,
            sourceFlags,
            settings.AmllApiBaseUrl);
    }

    private static string BuildCandidateCacheKey(
        MediaInfo info,
        LyricsSourceSettings settings,
        LyricsSourceId providerId)
    {
        return string.Join(
            "\u001f",
            LyricsTextNormalizer.NormalizeComparableText(info.Title),
            LyricsTextNormalizer.NormalizeComparableText(info.Artist),
            LyricsTextNormalizer.NormalizeComparableText(info.AlbumTitle),
            GetDurationBucket(info),
            providerId,
            settings.AmllApiBaseUrl);
    }

    private static string GetDurationBucket(MediaInfo info)
    {
        return info.Duration > TimeSpan.Zero
            ? ((int)Math.Round(info.Duration.TotalSeconds / 5.0) * 5).ToString()
            : "0";
    }

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

public sealed record LyricsSearchResult(
    LyricsDocument Document,
    string Id,
    string Title,
    string Artist,
    TimeSpan Duration,
    int? Score,
    LyricsSourceId Source);

public sealed class LyricsSearchResultChangedEventArgs(LyricsSearchResult? result) : EventArgs
{
    public LyricsSearchResult? Result { get; } = result;
}
