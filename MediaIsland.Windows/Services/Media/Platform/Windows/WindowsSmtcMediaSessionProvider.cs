using MediaIsland.Windows.Helpers;
using Microsoft.Extensions.Logging;
using Windows.Media.Control;
using WindowsMediaController;

namespace MediaIsland.Services.Media.Platform.Windows;

public sealed class WindowsSmtcMediaSessionProvider(
    ILogger<WindowsSmtcMediaSessionProvider> logger) : IMediaSessionProvider
{
    private readonly MediaManager _mediaManager = new();

    public event EventHandler<MediaSessionSnapshotEventArgs>? FocusedSessionChanged;

    public event EventHandler<MediaSessionSnapshotEventArgs>? MediaPropertiesChanged;

    public event EventHandler<MediaSessionSnapshotEventArgs>? PlaybackChanged;

    public event EventHandler<MediaSessionSnapshotEventArgs>? TimelineChanged;

    public bool IsStarted => _mediaManager.IsStarted;

    public MediaSessionSnapshot? CurrentSnapshot { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_mediaManager.IsStarted)
        {
            return;
        }

        _mediaManager.Logger = logger;
        _mediaManager.OnAnySessionOpened += OnAnySessionOpened;
        _mediaManager.OnAnySessionClosed += OnAnySessionClosed;
        _mediaManager.OnFocusedSessionChanged += OnFocusedSessionChanged;
        _mediaManager.OnAnyPlaybackStateChanged += OnAnyPlaybackStateChanged;
        _mediaManager.OnAnyMediaPropertyChanged += OnAnyMediaPropertyChanged;
        _mediaManager.OnAnyTimelinePropertyChanged += OnAnyTimelinePropertyChanged;

        cancellationToken.ThrowIfCancellationRequested();
        await _mediaManager.StartAsync();
        CurrentSnapshot = await GetCurrentSnapshotAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_mediaManager.IsStarted)
        {
            return Task.CompletedTask;
        }

        _mediaManager.OnAnySessionOpened -= OnAnySessionOpened;
        _mediaManager.OnAnySessionClosed -= OnAnySessionClosed;
        _mediaManager.OnFocusedSessionChanged -= OnFocusedSessionChanged;
        _mediaManager.OnAnyPlaybackStateChanged -= OnAnyPlaybackStateChanged;
        _mediaManager.OnAnyMediaPropertyChanged -= OnAnyMediaPropertyChanged;
        _mediaManager.OnAnyTimelinePropertyChanged -= OnAnyTimelinePropertyChanged;
        _mediaManager.Dispose();
        CurrentSnapshot = null;
        return Task.CompletedTask;
    }

    public async Task<MediaSessionSnapshot?> GetCurrentSnapshotAsync(CancellationToken cancellationToken = default)
    {
        if (!_mediaManager.IsStarted)
        {
            return null;
        }

        return await BuildSnapshotAsync(_mediaManager.GetFocusedSession(), cancellationToken);
    }

    private async void OnAnySessionOpened(MediaManager.MediaSession session)
    {
        logger.LogDebug("SMTC session opened: {SessionId}", session.Id);

        if (!IsFocusedSession(session))
        {
            return;
        }

        var snapshot = await BuildSnapshotAsync(session, CancellationToken.None);
        CurrentSnapshot = snapshot;
        Raise(FocusedSessionChanged, snapshot);
    }

    private void OnAnySessionClosed(MediaManager.MediaSession session)
    {
        logger.LogDebug("SMTC session closed: {SessionId}", session.Id);
    }

    private async void OnFocusedSessionChanged(MediaManager.MediaSession session)
    {
        logger.LogDebug("SMTC focused session changed: {SessionId}", session?.Id);
        var snapshot = await BuildSnapshotAsync(session, CancellationToken.None);
        CurrentSnapshot = snapshot;
        Raise(FocusedSessionChanged, snapshot);
    }

    private async void OnAnyPlaybackStateChanged(
        MediaManager.MediaSession session,
        GlobalSystemMediaTransportControlsSessionPlaybackInfo playbackInfo)
    {
        if (!IsFocusedSession(session))
        {
            return;
        }

        var snapshot = await BuildSnapshotAsync(session, CancellationToken.None);
        CurrentSnapshot = snapshot;
        Raise(PlaybackChanged, snapshot);
    }

    private async void OnAnyMediaPropertyChanged(
        MediaManager.MediaSession session,
        GlobalSystemMediaTransportControlsSessionMediaProperties mediaProperties)
    {
        if (!IsFocusedSession(session))
        {
            return;
        }

        var snapshot = await BuildSnapshotAsync(session, CancellationToken.None);
        CurrentSnapshot = snapshot;
        Raise(MediaPropertiesChanged, snapshot);
    }

    private async void OnAnyTimelinePropertyChanged(
        MediaManager.MediaSession session,
        GlobalSystemMediaTransportControlsSessionTimelineProperties timelineProperties)
    {
        if (!IsFocusedSession(session))
        {
            return;
        }

        var snapshot = await BuildSnapshotAsync(session, CancellationToken.None);
        CurrentSnapshot = snapshot;
        Raise(TimelineChanged, snapshot);
    }

    private bool IsFocusedSession(MediaManager.MediaSession session)
    {
        try
        {
            return ReferenceEquals(session, _mediaManager.GetFocusedSession());
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to check focused SMTC session.");
            return false;
        }
    }

    private async Task<MediaSessionSnapshot?> BuildSnapshotAsync(
        MediaManager.MediaSession? session,
        CancellationToken cancellationToken)
    {
        if (session?.ControlSession == null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var controlSession = session.ControlSession;
        var mediaProperties = await controlSession.TryGetMediaPropertiesAsync();
        var timelineProperties = controlSession.GetTimelineProperties();
        var playbackInfo = controlSession.GetPlaybackInfo();
        var thumbnailReference = mediaProperties.Thumbnail;
        MediaThumbnail? thumbnail = thumbnailReference == null
            ? null
            : new MediaThumbnail(async (isSourceAppSpotify, token) =>
            {
                token.ThrowIfCancellationRequested();
                var bitmap = await WinRtThumbnailHelper.GetThumbnail(
                    thumbnailReference,
                    isSourceAppSpotify,
                    logger);
                token.ThrowIfCancellationRequested();
                return bitmap;
            });

        return new MediaSessionSnapshot(
            controlSession.SourceAppUserModelId,
            mediaProperties.Title,
            mediaProperties.Artist,
            mediaProperties.AlbumTitle,
            timelineProperties.Position,
            timelineProperties.EndTime,
            new MediaPlaybackInfo(
                MapPlaybackState(playbackInfo.PlaybackStatus),
                playbackInfo.PlaybackRate),
            thumbnail);
    }

    private static MediaPlaybackState MapPlaybackState(GlobalSystemMediaTransportControlsSessionPlaybackStatus status)
    {
        return status switch
        {
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed => MediaPlaybackState.Closed,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Opened => MediaPlaybackState.Opened,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing => MediaPlaybackState.Changing,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => MediaPlaybackState.Stopped,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => MediaPlaybackState.Playing,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => MediaPlaybackState.Paused,
            _ => MediaPlaybackState.Unknown
        };
    }

    private void Raise(
        EventHandler<MediaSessionSnapshotEventArgs>? handlers,
        MediaSessionSnapshot? snapshot)
    {
        if (handlers == null)
        {
            return;
        }

        var args = new MediaSessionSnapshotEventArgs(snapshot);
        foreach (EventHandler<MediaSessionSnapshotEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "A Windows SMTC provider subscriber failed.");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
