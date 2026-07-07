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
        try
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
        catch (Exception ex)
        {
            logger.LogWarning(ex, "处理 SMTC 会话打开事件失败：{SessionId}", session.Id);
        }
    }

    private void OnAnySessionClosed(MediaManager.MediaSession session)
    {
        logger.LogDebug("SMTC session closed: {SessionId}", session.Id);
    }

    private async void OnFocusedSessionChanged(MediaManager.MediaSession session)
    {
        try
        {
            logger.LogDebug("SMTC focused session changed: {SessionId}", session?.Id);
            var snapshot = await BuildSnapshotAsync(session, CancellationToken.None);
            CurrentSnapshot = snapshot;
            Raise(FocusedSessionChanged, snapshot);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "处理 SMTC 焦点会话改变事件失败：{SessionId}", session?.Id);
        }
    }

    private async void OnAnyPlaybackStateChanged(
        MediaManager.MediaSession session,
        GlobalSystemMediaTransportControlsSessionPlaybackInfo playbackInfo)
    {
        try
        {
            if (!IsFocusedSession(session))
            {
                return;
            }

            var snapshot = await BuildSnapshotAsync(session, CancellationToken.None);
            CurrentSnapshot = snapshot;
            Raise(PlaybackChanged, snapshot);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "处理 SMTC 播放状态改变事件失败：{SessionId}", session.Id);
        }
    }

    private async void OnAnyMediaPropertyChanged(
        MediaManager.MediaSession session,
        GlobalSystemMediaTransportControlsSessionMediaProperties mediaProperties)
    {
        try
        {
            if (!IsFocusedSession(session))
            {
                return;
            }

            var snapshot = await BuildSnapshotAsync(session, CancellationToken.None);
            CurrentSnapshot = snapshot;
            Raise(MediaPropertiesChanged, snapshot);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "处理 SMTC 媒体属性改变事件失败：{SessionId}", session.Id);
        }
    }

    private async void OnAnyTimelinePropertyChanged(
        MediaManager.MediaSession session,
        GlobalSystemMediaTransportControlsSessionTimelineProperties timelineProperties)
    {
        try
        {
            if (!IsFocusedSession(session))
            {
                return;
            }

            var snapshot = await BuildSnapshotAsync(session, CancellationToken.None);
            CurrentSnapshot = snapshot;
            Raise(TimelineChanged, snapshot);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "处理 SMTC 时间轴改变事件失败：{SessionId}", session.Id);
        }
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

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var controlSession = session.ControlSession;
            var sourceApp = controlSession.SourceAppUserModelId;
            var mediaProperties = await TryGetMediaPropertiesAsync(controlSession, sourceApp, cancellationToken);
            var timelineProperties = TryGetTimelineProperties(controlSession, sourceApp);
            var playbackInfo = TryGetPlaybackInfo(controlSession, sourceApp);
            var thumbnailReference = mediaProperties?.Thumbnail;
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
                sourceApp,
                mediaProperties?.Title,
                mediaProperties?.Artist,
                mediaProperties?.AlbumTitle,
                timelineProperties.Position,
                timelineProperties.EndTime,
                playbackInfo,
                thumbnail);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "无法生成 SMTC 媒体会话快照：{SessionId}", session.Id);
            return null;
        }
    }

    private async Task<GlobalSystemMediaTransportControlsSessionMediaProperties?> TryGetMediaPropertiesAsync(
        GlobalSystemMediaTransportControlsSession controlSession,
        string sourceApp,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await controlSession.TryGetMediaPropertiesAsync();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "无法读取 SMTC 媒体属性：{SourceApp}", sourceApp);
            return null;
        }
    }

    private (TimeSpan Position, TimeSpan EndTime) TryGetTimelineProperties(
        GlobalSystemMediaTransportControlsSession controlSession,
        string sourceApp)
    {
        try
        {
            var timelineProperties = controlSession.GetTimelineProperties();
            return (timelineProperties.Position, timelineProperties.EndTime);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "无法读取 SMTC 时间轴属性：{SourceApp}", sourceApp);
            return (TimeSpan.Zero, TimeSpan.Zero);
        }
    }

    private MediaPlaybackInfo TryGetPlaybackInfo(
        GlobalSystemMediaTransportControlsSession controlSession,
        string sourceApp)
    {
        try
        {
            var playbackInfo = controlSession.GetPlaybackInfo();
            return new MediaPlaybackInfo(
                MapPlaybackState(playbackInfo.PlaybackStatus),
                playbackInfo.PlaybackRate);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "无法读取 SMTC 播放状态：{SourceApp}", sourceApp);
            return new MediaPlaybackInfo(MediaPlaybackState.Unknown);
        }
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
