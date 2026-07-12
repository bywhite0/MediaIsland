using System.Collections.Concurrent;
using MediaIsland.Services.Lyrics.Models;
using MediaIsland.Services.Media;
using Microsoft.Extensions.Logging;

namespace MediaIsland.Services.Lyrics;

/// <summary>
/// Multi-source lyric search coordinator with deterministic user priority.
/// </summary>
public sealed class LyricsSearchService
{
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
                _logger?.LogInformation("[Lyrics] No enabled lyric sources.");
                return null;
            }

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var searchTasks = orderedProviders
                .Select(provider => SearchProviderCandidatesAsync(provider, info, settings, linkedCts.Token))
                .ToArray();

            await Task.WhenAll(searchTasks);
            cancellationToken.ThrowIfCancellationRequested();

            var providerCandidates = searchTasks
                .Select((task, index) => (Provider: orderedProviders[index], Candidates: task.Result))
                .ToArray();

            foreach (var entry in providerCandidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!settings.PreferWordSync(entry.Provider.Id))
                {
                    continue;
                }

                var result = await TrySelectFromProviderAsync(
                    entry.Provider,
                    entry.Candidates,
                    settings,
                    preferWordOnly: true,
                    allowLineFallback: false,
                    cancellationToken);
                if (result != null)
                {
                    Cache(cacheKey, result, success: true);
                    PublishCurrentResult(result, searchVersion);
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
                    Cache(cacheKey, result, success: true);
                    PublishCurrentResult(result, searchVersion);
                    return result;
                }
            }

            _logger?.LogInformation("[Lyrics] Not found for {Title} - {Artist}", info.Title, info.Artist);
            Cache(cacheKey, null, success: false);
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[Lyrics] Error while searching lyrics.");
            PublishCurrentResult(null, searchVersion);
            return null;
        }
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
        CancellationToken cancellationToken)
    {
        try
        {
            var candidates = await provider.SearchAsync(info, settings, cancellationToken);
            return candidates
                .Where(candidate => candidate.Score >= LyricsCandidateScorer.MinimumScore(info))
                .OrderByDescending(candidate => candidate.Score)
                .ToArray();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger?.LogWarning("[Lyrics:{Provider}] Search timed out.", provider.Id);
            return [];
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[Lyrics:{Provider}] Search failed.", provider.Id);
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
                    "[Lyrics:{Provider}] Fetch timed out for {Id}",
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
                _logger?.LogWarning(ex, "[Lyrics:{Provider}] Fetch failed for {Id}", provider.Id, candidate.ProviderItemId);
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
                _logger?.LogWarning("[Lyrics:{Provider}] No parser for format {Format}", provider.Id, payload.Format);
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
                _logger?.LogWarning(ex, "[Lyrics:{Provider}] Parse failed for {Id}", provider.Id, candidate.ProviderItemId);
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
                "[Lyrics] Selected {Source}/{Format} sync={Sync} id={Id} score={Score} title={Title}",
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

    private void Cache(string key, LyricsSearchResult? result, bool success)
    {
        var ttl = success ? TimeSpan.FromMinutes(30) : TimeSpan.FromMinutes(2);
        _cache[key] = new CacheEntry(result, DateTimeOffset.UtcNow.Add(ttl));
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
                _logger?.LogWarning(ex, "[Lyrics] Current result subscriber failed.");
            }
        }
    }

    private sealed record CacheEntry(LyricsSearchResult? Result, DateTimeOffset ExpiresAt);
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
