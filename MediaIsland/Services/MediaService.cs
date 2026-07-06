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
    private readonly PluginSettings _globalSettings = Plugin.globalConfigFolder is null
        ? new PluginSettings()
        : ConfigureFileHelper.LoadConfig<PluginSettings>(Path.Combine(Plugin.globalConfigFolder, "Settings.json"));

    public MediaInfo? CurrentMediaInfo { get; private set; }

    public event EventHandler<MediaInfo?>? OnMediaPropertiesChanged;
    public event EventHandler<GlobalSystemMediaTransportControlsSessionPlaybackInfo>? OnPlaybackStateChanged;
    public event EventHandler? OnFocusedSessionChanged;
    public event EventHandler<GlobalSystemMediaTransportControlsSessionTimelineProperties>? OnTimelinePropertyChanged;

    private readonly object _startSync = new();
    private Task? _startTask;
    private bool _mediaManagerEventsSubscribed;
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
            await GetOrCreateStartTask().ConfigureAwait(false);
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

    private Task GetOrCreateStartTask()
    {
        lock (_startSync)
        {
            if (_disposed == 1 || _mediaManager.IsStarted)
            {
                return Task.CompletedTask;
            }

            return _startTask ??= StartMediaManagerAsync();
        }
    }

    private async Task StartMediaManagerAsync()
    {
        try
        {
            if (!EnsureMediaManagerEventsSubscribed())
            {
                return;
            }

            await _mediaManager.StartAsync().ConfigureAwait(false);
            if (_disposed == 1 && _mediaManager.IsStarted)
            {
                _mediaManager.Dispose();
                lock (_startSync)
                {
                    _mediaManagerEventsSubscribed = false;
                }
            }

            if (_disposed == 1 || !_mediaManager.IsStarted)
            {
                return;
            }

            var currentSession = _mediaManager.GetFocusedSession();
            if (currentSession != null)
            {
                await RefreshMediaInfo(currentSession).ConfigureAwait(false);
            }
        }
        catch (COMException)
        {
            ResetStartTaskIfNotStarted();
            logger.LogWarning("Unable to get SMTC session manager.");
        }
        catch (Exception ex)
        {
            ResetStartTaskIfNotStarted();
            logger.LogError(ex, "An error occurred while starting SMTC session manager.");
        }
    }

    private void ResetStartTaskIfNotStarted()
    {
        lock (_startSync)
        {
            if (!_mediaManager.IsStarted)
            {
                _startTask = null;
            }
        }
    }

    private bool EnsureMediaManagerEventsSubscribed()
    {
        lock (_startSync)
        {
            if (_disposed == 1)
            {
                return false;
            }

            if (_mediaManagerEventsSubscribed)
            {
                return true;
            }

            _mediaManager.OnAnySessionOpened += OnAnySessionOpened;
            _mediaManager.OnAnySessionClosed += OnAnySessionClosed;
            _mediaManager.OnFocusedSessionChanged += OnCurrentSessionChanged;
            _mediaManager.OnAnyPlaybackStateChanged += OnAnyPlaybackStateChanged;
            _mediaManager.OnAnyTimelinePropertyChanged += OnAnyTimelinePropertyChanged;
            _mediaManager.OnAnyMediaPropertyChanged += OnAnyMediaPropertyChanged;
            _mediaManagerEventsSubscribed = true;
            return true;
        }
    }

    private void UnsubscribeMediaManagerEvents()
    {
        lock (_startSync)
        {
            if (!_mediaManagerEventsSubscribed)
            {
                return;
            }

            _mediaManager.OnAnySessionOpened -= OnAnySessionOpened;
            _mediaManager.OnAnySessionClosed -= OnAnySessionClosed;
            _mediaManager.OnFocusedSessionChanged -= OnCurrentSessionChanged;
            _mediaManager.OnAnyPlaybackStateChanged -= OnAnyPlaybackStateChanged;
            _mediaManager.OnAnyTimelinePropertyChanged -= OnAnyTimelinePropertyChanged;
            _mediaManager.OnAnyMediaPropertyChanged -= OnAnyMediaPropertyChanged;
            _mediaManagerEventsSubscribed = false;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        UnsubscribeMediaManagerEvents();
        _mediaManager.Dispose();

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
            RaiseEvent(OnFocusedSessionChanged, EventArgs.Empty, "A focused session changed subscriber failed.");
            RaiseMediaPropertiesChanged(null);
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
        RaiseEvent(OnPlaybackStateChanged, args, "A playback state changed subscriber failed.");
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

        UpdateCurrentTimeline(args);
        RaiseEvent(OnTimelinePropertyChanged, args, "A timeline property changed subscriber failed.");
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

    private void UpdateCurrentTimeline(GlobalSystemMediaTransportControlsSessionTimelineProperties timeline)
    {
        var current = CurrentMediaInfo;
        if (current == null)
        {
            return;
        }

        CurrentMediaInfo = new MediaInfo(
            current.Title,
            current.Artist,
            current.AlbumTitle,
            timeline.Position,
            timeline.EndTime,
            current.SourceApp,
            current.PlaybackInfo,
            current.Thumbnail
        );
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
            RaiseMediaPropertiesChanged(null);
            return;
        }

        MediaInfo data;
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

            data = new MediaInfo(
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
        }
        catch (OperationCanceledException) when (_disposed == 1)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get SMTC info.");
            CurrentMediaInfo = null; // Clear current media info on error
            RaiseMediaPropertiesChanged(null);
            return;
        }

        RaiseMediaPropertiesChanged(data);
    }

    private void RaiseMediaPropertiesChanged(MediaInfo? info)
    {
        RaiseEvent(OnMediaPropertiesChanged, info, "A media properties changed subscriber failed.");
    }

    private void RaiseEvent(EventHandler? eventHandler, EventArgs args, string failureMessage)
    {
        var handlers = eventHandler?.GetInvocationList();
        if (handlers == null)
        {
            return;
        }

        foreach (EventHandler handler in handlers)
        {
            try
            {
                handler(this, args);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, failureMessage);
            }
        }
    }

    private void RaiseEvent<TEventArgs>(EventHandler<TEventArgs>? eventHandler, TEventArgs args, string failureMessage)
    {
        var handlers = eventHandler?.GetInvocationList();
        if (handlers == null)
        {
            return;
        }

        foreach (EventHandler<TEventArgs> handler in handlers)
        {
            try
            {
                handler(this, args);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, failureMessage);
            }
        }
    }
}
