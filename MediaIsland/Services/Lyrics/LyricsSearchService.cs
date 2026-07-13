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
    private const int MaxSearchAttempts = 3;
    private static readonly TimeSpan SearchRetryDelay = TimeSpan.FromMilliseconds(300);
    private readonly IReadOnlyList<ILyricsProvider> _providers;
    private readonly IReadOnlyList<ILyricsPayloadParser> _parsers;
    private readonly Func<LyricsSourceSettings> _settingsFactory;
    private readonly ILogger<LyricsSearchService>? _logger;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private string _settingsFingerprint = string.Empty;
    private LyricsSearchResult? _currentResult;
    private string? _currentMediaTitle;
    private string? _currentMediaArtist;
    private long _searchVersion;

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
        var searchVersion = Interlocked.Increment(ref _searchVersion);
        Interlocked.Exchange(ref _currentMediaTitle, info.Title);
        Interlocked.Exchange(ref _currentMediaArtist, info.Artist);
        PublishCurrentResult(null, searchVersion);

        if (string.IsNullOrWhiteSpace(info.Title))
        {
            return null;
        }

        var settings = LyricsSourceSettings.Normalize(_settingsFactory().Clone());
        EnsureCacheFingerprint(settings);

        var cacheKey = BuildCacheKey(info, settings);
        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            if (cached.ExpiresAt > DateTimeOffset.UtcNow)
            {
                PublishCurrentResult(cached.Result, searchVersion);
                return cached.Result;
            }

            _cache.TryRemove(cacheKey, out _);
        }

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

            for (var attempt = 1; attempt <= MaxSearchAttempts; attempt++)
            {
                var result = await SearchOnceAsync(orderedProviders, info, settings, cancellationToken);
                if (result != null)
                {
                    Cache(cacheKey, result);
                    PublishCurrentResult(result, searchVersion);
                    return result;
                }

                if (attempt < MaxSearchAttempts)
                {
                    _logger?.LogInformation(
                        "[歌词] 第 {Attempt}/{Total} 次搜索未找到歌词，准备重试：{Title} - {Artist}",
                        attempt,
                        MaxSearchAttempts,
                        info.Title,
                        info.Artist);
                    await Task.Delay(SearchRetryDelay, cancellationToken);
                }
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

    public async Task<IReadOnlyList<LyricsCandidate>> SearchCandidatesAsync(
        MediaInfo info,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(info.Title))
        {
            return [];
        }

        var settings = LyricsSourceSettings.Normalize(_settingsFactory().Clone());
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

        var searchVersion = Interlocked.Increment(ref _searchVersion);
        Interlocked.Exchange(ref _currentMediaTitle, info.Title);
        Interlocked.Exchange(ref _currentMediaArtist, info.Artist);
        EnsureCacheFingerprint(settings);

        var result = await TrySelectFromProviderAsync(
            provider,
            [candidate],
            settings,
            preferWordOnly: false,
            allowLineFallback: true,
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
                preferWordOnly: true,
                allowLineFallback: false,
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
                preferWordOnly: false,
                allowLineFallback: true,
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
        _settingsFingerprint = string.Empty;
    }

    private async Task<IReadOnlyList<LyricsCandidate>> SearchProviderCandidatesAsync(
        ILyricsProvider provider,
        MediaInfo info,
        LyricsSourceSettings settings,
        CancellationToken cancellationToken,
        bool includeBelowMinimumScore = false)
    {
        try
        {
            var candidates = await provider.SearchAsync(info, settings, cancellationToken);
            var orderedCandidates = candidates
                .OrderByDescending(candidate => candidate.Score);
            return includeBelowMinimumScore
                ? orderedCandidates.ToArray()
                : orderedCandidates
                    .Where(candidate => candidate.Score >= LyricsCandidateScorer.MinimumScore(info))
                    .ToArray();
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

    private async Task<LyricsSearchResult?> TrySelectFromProviderAsync(
        ILyricsProvider provider,
        IReadOnlyList<LyricsCandidate> candidates,
        LyricsSourceSettings settings,
        bool preferWordOnly,
        bool allowLineFallback,
        CancellationToken cancellationToken)
    {
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (preferWordOnly && !candidate.SupportsWordSync)
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
            if (preferWordOnly && !isWordPayload && !allowLineFallback)
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

            if (preferWordOnly && document.SyncMode != LyricsSyncMode.Word && !allowLineFallback)
            {
                continue;
            }

            if (!settings.PreferWordSync(provider.Id) && document.SyncMode == LyricsSyncMode.Word)
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
            _settingsFingerprint = fingerprint;
        }
    }

    private static string BuildCacheKey(MediaInfo info, LyricsSourceSettings settings)
    {
        var durationBucket = info.Duration > TimeSpan.Zero
            ? ((int)Math.Round(info.Duration.TotalSeconds / 5.0) * 5).ToString()
            : "0";
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

        var args = new LyricsSearchResultChangedEventArgs(result);
        foreach (EventHandler<LyricsSearchResultChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[歌词] 当前结果订阅者执行失败。");
            }
        }
    }

    private void PublishCandidateApplied(LyricsSearchResult result)
    {
        var handlers = CandidateApplied;
        if (handlers == null)
        {
            return;
        }

        var args = new LyricsSearchResultChangedEventArgs(result);
        foreach (EventHandler<LyricsSearchResultChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[歌词] 手动应用歌词订阅者执行失败。");
            }
        }
    }

    private sealed record CacheEntry(LyricsSearchResult Result, DateTimeOffset ExpiresAt);
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
