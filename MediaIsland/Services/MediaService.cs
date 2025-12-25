using System.Runtime.InteropServices;
using Windows.Media.Control;
using MediaIsland.Helpers;
using MediaIsland.Models;
using Microsoft.Extensions.Logging;
using WindowsMediaController;
using static WindowsMediaController.MediaManager;

namespace MediaIsland.Services;

public class MediaService(ILogger<MediaService> logger) : IMediaService
{
    private readonly MediaManager _mediaManager = new();

    public MediaInfo? CurrentMediaInfo { get; private set; }

    public event EventHandler<MediaInfo?>? OnMediaPropertiesChanged;
    public event EventHandler<GlobalSystemMediaTransportControlsSessionPlaybackInfo>? OnPlaybackStateChanged;
    public event EventHandler? OnFocusedSessionChanged;

    private int _disposed;

    public async Task StartAsync()
    {
        try
        {
            if (!_mediaManager.IsStarted)
            {
                _mediaManager.OnAnySessionOpened += OnAnySessionOpened; 
                _mediaManager.OnAnySessionClosed += OnAnySessionClosed;
                _mediaManager.OnFocusedSessionChanged += OnCurrentSessionChanged;
                _mediaManager.OnAnyPlaybackStateChanged += OnAnyPlaybackStateChanged;
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
            _mediaManager.OnAnyMediaPropertyChanged -= OnAnyMediaPropertyChanged;
            _mediaManager.Dispose();
        }
    }
    
    private void OnAnySessionOpened(MediaSession sender)
    {
        logger.LogDebug($"New SMTC session: {sender.Id}");
        RefreshMediaInfo(sender);
    }

    private void OnAnySessionClosed(MediaSession sender)
    {
        logger.LogDebug($"SMTC session closed: {sender.Id}");
        OnFocusedSessionChanged?.Invoke(this, EventArgs.Empty);
    }
    
    private void OnCurrentSessionChanged(MediaSession? sender)
    {
        logger.LogDebug($"Focused SMTC session changed: {sender?.ControlSession?.SourceAppUserModelId}");
        if (sender?.ControlSession == null)
        {
            CurrentMediaInfo = null; // Clear current media info
            OnFocusedSessionChanged?.Invoke(this, EventArgs.Empty);
            OnMediaPropertiesChanged?.Invoke(this, null);
        }
        else
        {
            RefreshMediaInfo(sender);
        }
    }

    private void OnAnyPlaybackStateChanged(MediaSession sender, GlobalSystemMediaTransportControlsSessionPlaybackInfo args)
    {
        logger.LogDebug($"SMTC playback state changed: {sender.Id} is now {args.PlaybackStatus}");
        OnPlaybackStateChanged?.Invoke(this, args);
        RefreshMediaInfo(sender);
    }

    private void OnAnyMediaPropertyChanged(MediaSession sender, GlobalSystemMediaTransportControlsSessionMediaProperties args)
    {
        logger.LogDebug($"SMTC media properties changed: {sender.Id} is now playing {args.Title} {(string.IsNullOrEmpty(args.Artist) ? "" : $"by {args.Artist}")}");
        RefreshMediaInfo(sender);
    }
    
    private async Task RefreshMediaInfo(MediaSession session)
    {
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
                AppInfoHelper.IsSourceAppSpotify(sourceApp));

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
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get SMTC info.");
            CurrentMediaInfo = null; // Clear current media info on error
            OnMediaPropertiesChanged?.Invoke(this, null);
        }
    }
}