using System.IO;
using System.Runtime.InteropServices;
using ClassIsland.Shared.Helpers;
using Windows.Media.Control;
using MediaIsland.Helpers;
using MediaIsland.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WindowsMediaController;
using static WindowsMediaController.MediaManager;

namespace MediaIsland.Services;

public class MediaService(ILogger<MediaService> logger) : IMediaService, IHostedService
{
    private readonly MediaManager _mediaManager = new()
    {
        Logger = logger
    };
    private readonly PluginSettings _globalSettings =
        ConfigureFileHelper.LoadConfig<PluginSettings>(Path.Combine(Plugin.globalConfigFolder!, "Settings.json"));

    public MediaInfo? CurrentMediaInfo { get; private set; }

    public event EventHandler<MediaInfo?>? OnMediaPropertiesChanged;
    public event EventHandler<GlobalSystemMediaTransportControlsSessionPlaybackInfo>? OnPlaybackStateChanged;
    public event EventHandler? OnFocusedSessionChanged;
    public event EventHandler<GlobalSystemMediaTransportControlsSessionTimelineProperties>? OnTimelinePropertyChanged;

    private int _disposed;

    Task IHostedService.StartAsync(CancellationToken cancellationToken)
    {
        return cancellationToken.IsCancellationRequested ? Task.CompletedTask : StartAsync();
    }

    Task IHostedService.StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public async Task StartAsync()
    {
        if (_disposed == 1)
        {
            return;
        }

        try
        {
            if (!_mediaManager.IsStarted)
            {
                _mediaManager.OnAnySessionOpened += OnAnySessionOpened;
                _mediaManager.OnAnySessionClosed += OnAnySessionClosed;
                _mediaManager.OnFocusedSessionChanged += OnCurrentSessionChanged;
                _mediaManager.OnAnyPlaybackStateChanged += OnAnyPlaybackStateChanged;
                _mediaManager.OnAnyTimelinePropertyChanged += OnAnyTimelinePropertyChanged;
                _mediaManager.OnAnyMediaPropertyChanged += OnAnyMediaPropertyChanged;
                await _mediaManager.StartAsync();
            }
            var currentSession = _mediaManager.GetFocusedSession();
            if (currentSession != null)
            {
                await RefreshMediaInfo(currentSession);
            }
        }
        catch (COMException)
        {
            logger.LogWarning("Unable to get SMTC session manager.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An error occurred while getting SMTC session.");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        if (_mediaManager.IsStarted)
        {
            _mediaManager.OnAnySessionOpened -= OnAnySessionOpened;
            _mediaManager.OnAnySessionClosed -= OnAnySessionClosed;
            _mediaManager.OnFocusedSessionChanged -= OnCurrentSessionChanged;
            _mediaManager.OnAnyPlaybackStateChanged -= OnAnyPlaybackStateChanged;
            _mediaManager.OnAnyTimelinePropertyChanged -= OnAnyTimelinePropertyChanged;
            _mediaManager.OnAnyMediaPropertyChanged -= OnAnyMediaPropertyChanged;
            _mediaManager.Dispose();
        }

        CurrentMediaInfo = null;
        OnMediaPropertiesChanged = null;
        OnPlaybackStateChanged = null;
        OnFocusedSessionChanged = null;
        OnTimelinePropertyChanged = null;
    }

    private void OnAnySessionOpened(MediaSession sender)
    {
        if (_disposed == 1)
        {
            return;
        }

        logger.LogDebug($"New SMTC session: {sender.Id}");
        if (!IsFocusedSession(sender))
        {
            return;
        }

        _ = RefreshMediaInfo(sender);
    }

    private void OnAnySessionClosed(MediaSession sender)
    {
        if (_disposed == 1)
        {
            return;
        }

        logger.LogDebug($"SMTC session closed: {sender.Id}");
        OnFocusedSessionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnCurrentSessionChanged(MediaSession? sender)
    {
        if (_disposed == 1)
        {
            return;
        }

        logger.LogDebug($"Focused SMTC session changed: {sender?.ControlSession?.SourceAppUserModelId}");
        if (sender?.ControlSession == null)
        {
            CurrentMediaInfo = null; // Clear current media info
            OnFocusedSessionChanged?.Invoke(this, EventArgs.Empty);
            OnMediaPropertiesChanged?.Invoke(this, null);
        }
        else
        {
            _ = RefreshMediaInfo(sender);
        }
    }

    private void OnAnyPlaybackStateChanged(MediaSession sender, GlobalSystemMediaTransportControlsSessionPlaybackInfo args)
    {
        if (_disposed == 1)
        {
            return;
        }

        if (!IsFocusedSession(sender))
        {
            return;
        }

        logger.LogDebug($"SMTC playback state changed: {sender.Id} is now {args.PlaybackStatus}");
        OnPlaybackStateChanged?.Invoke(this, args);
        _ = RefreshMediaInfo(sender);
    }

    private void OnAnyTimelinePropertyChanged(MediaSession sender, GlobalSystemMediaTransportControlsSessionTimelineProperties args)
    {
        if (_disposed == 1)
        {
            return;
        }

        if (!IsFocusedSession(sender))
        {
            return;
        }

        OnTimelinePropertyChanged?.Invoke(this, args);
        _ = RefreshMediaInfo(sender);
    }

    private void OnAnyMediaPropertyChanged(MediaSession sender, GlobalSystemMediaTransportControlsSessionMediaProperties args)
    {
        if (_disposed == 1)
        {
            return;
        }

        if (!IsFocusedSession(sender))
        {
            return;
        }

        logger.LogDebug($"SMTC media properties changed: {sender.Id} is now playing {args.Title} {(string.IsNullOrEmpty(args.Artist) ? "" : $"by {args.Artist}")}");
        _ = RefreshMediaInfo(sender);
    }

    private bool IsFocusedSession(MediaSession sender)
    {
        if (_disposed == 1 || !_mediaManager.IsStarted)
        {
            return false;
        }

        return sender == _mediaManager.GetFocusedSession();
    }

    private async Task RefreshMediaInfo(MediaSession session)
    {
        if (_disposed == 1)
        {
            return;
        }

        if (session?.ControlSession == null)
        {
            CurrentMediaInfo = null; // Clear current media info
            OnMediaPropertiesChanged?.Invoke(this, null);
            return;
        }

        try
        {
            var sourceApp = session.ControlSession.SourceAppUserModelId;

            var mediaProperties = await session.ControlSession.TryGetMediaPropertiesAsync();
            var timeline = session.ControlSession.GetTimelineProperties();
            var playbackInfo = session.ControlSession.GetPlaybackInfo();
            var thumbnail = await ThumbnailHelper.GetThumbnail(mediaProperties.Thumbnail,
                AppInfoHelper.IsSourceAppSpotify(sourceApp) && _globalSettings.IsCutSpotifyTrademarkEnabled);

            if (_disposed == 1)
            {
                return;
            }

            var data = new MediaInfo(
                mediaProperties.Title ?? "未知标题",
                mediaProperties.Artist ?? "未知艺术家",
                mediaProperties.AlbumTitle ?? "未知专辑",
                timeline.Position,
                timeline.EndTime,
                sourceApp,
                playbackInfo,
                thumbnail
            );

            CurrentMediaInfo = data; // Store the current media info
            OnMediaPropertiesChanged?.Invoke(this, data);
        }
        catch (OperationCanceledException) when (_disposed == 1)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get SMTC info.");
            CurrentMediaInfo = null; // Clear current media info on error
            OnMediaPropertiesChanged?.Invoke(this, null);
        }
    }
}
