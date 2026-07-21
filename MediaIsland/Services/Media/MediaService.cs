using MediaIsland.Services.Media.Platform;
using Microsoft.Extensions.Logging;

namespace MediaIsland.Services.Media;

public sealed class MediaService(
    MediaPlatformProviderResolver providerResolver,
    ILogger<MediaService> logger) : IMediaService, IDisposable
{
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private IMediaSessionProvider? _sessionProvider;
    private bool _isStarted;
    private bool _disposed;

    public event EventHandler<MediaInfoChangedEventArgs>? MediaInfoChanged;

    public MediaInfo? CurrentMediaInfo { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken);
    }

    public async Task EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            if (_isStarted)
            {
                return;
            }

            var platformProvider = providerResolver.Resolve();
            _sessionProvider = platformProvider.SessionProvider;
            _sessionProvider.FocusedSessionChanged += OnFocusedSessionChanged;
            _sessionProvider.MediaPropertiesChanged += OnMediaPropertiesChanged;
            _sessionProvider.PlaybackChanged += OnPlaybackChanged;
            _sessionProvider.TimelineChanged += OnTimelineChanged;

            await _sessionProvider.StartAsync(cancellationToken);
            _isStarted = true;

            var snapshot = await _sessionProvider.GetCurrentSnapshotAsync(cancellationToken);
            await RefreshCurrentMediaInfoAsync(snapshot, MediaInfoChangeKind.CurrentSession, cancellationToken);
        }
        catch
        {
            if (_sessionProvider != null)
            {
                Unsubscribe(_sessionProvider);
            }

            _sessionProvider = null;
            _isStarted = false;
            throw;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            if (!_isStarted || _sessionProvider == null)
            {
                return;
            }

            Unsubscribe(_sessionProvider);
            await _sessionProvider.StopAsync(cancellationToken);
            _sessionProvider = null;
            _isStarted = false;
            CurrentMediaInfo = null;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private void OnFocusedSessionChanged(object? sender, MediaSessionSnapshotEventArgs e)
    {
        _ = RefreshCurrentMediaInfoAsync(e.Snapshot, MediaInfoChangeKind.CurrentSession, CancellationToken.None);
    }

    private void OnMediaPropertiesChanged(object? sender, MediaSessionSnapshotEventArgs e)
    {
        _ = RefreshCurrentMediaInfoAsync(e.Snapshot, MediaInfoChangeKind.MediaProperties, CancellationToken.None);
    }

    private void OnPlaybackChanged(object? sender, MediaSessionSnapshotEventArgs e)
    {
        _ = RefreshCurrentMediaInfoAsync(e.Snapshot, MediaInfoChangeKind.Playback, CancellationToken.None);
    }

    private void OnTimelineChanged(object? sender, MediaSessionSnapshotEventArgs e)
    {
        _ = RefreshCurrentMediaInfoAsync(e.Snapshot, MediaInfoChangeKind.Timeline, CancellationToken.None);
    }

    private async Task RefreshCurrentMediaInfoAsync(
        MediaSessionSnapshot? snapshot,
        MediaInfoChangeKind changeKind,
        CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            CurrentMediaInfo = snapshot == null
                ? null
                : await CreateMediaInfoAsync(snapshot, cancellationToken);

            RaiseMediaInfoChanged(new MediaInfoChangedEventArgs(CurrentMediaInfo, changeKind));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to refresh media info.");
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private static async Task<MediaInfo> CreateMediaInfoAsync(
        MediaSessionSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var thumbnail = snapshot.Thumbnail == null
            ? null
            : await snapshot.Thumbnail.LoadBitmapAsync(false, cancellationToken);

        return new MediaInfo(
            snapshot.SourceApp,
            snapshot.Title,
            snapshot.Artist,
            snapshot.AlbumTitle,
            snapshot.Position,
            snapshot.Duration,
            snapshot.PlaybackInfo,
            thumbnail,
            snapshot.Thumbnail);
    }

    private void RaiseMediaInfoChanged(MediaInfoChangedEventArgs args)
    {
        var handlers = MediaInfoChanged;
        if (handlers == null)
        {
            return;
        }

        // Isolate component failures so one subscriber cannot stop other UI consumers.
        foreach (EventHandler<MediaInfoChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "A media service subscriber failed.");
            }
        }
    }

    private void Unsubscribe(IMediaSessionProvider sessionProvider)
    {
        sessionProvider.FocusedSessionChanged -= OnFocusedSessionChanged;
        sessionProvider.MediaPropertiesChanged -= OnMediaPropertiesChanged;
        sessionProvider.PlaybackChanged -= OnPlaybackChanged;
        sessionProvider.TimelineChanged -= OnTimelineChanged;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_sessionProvider != null)
        {
            Unsubscribe(_sessionProvider);
        }

        _lifecycleLock.Dispose();
        _refreshLock.Dispose();
    }
}
