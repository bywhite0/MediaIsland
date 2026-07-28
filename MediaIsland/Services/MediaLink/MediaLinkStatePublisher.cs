using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Media;
using MediaIsland.Services.MediaLink.Mapping;
using MediaIsland.Services.MediaLink.Protocol;
using Microsoft.Extensions.Logging;

namespace MediaIsland.Services.MediaLink;

public sealed class MediaLinkStatePublisher : IDisposable
{
    private readonly MediaSourceCoordinator _coordinator;
    private readonly MediaLinkSessionHub _hub;
    private readonly Func<int> _timelineMinIntervalMs;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly ILogger<MediaLinkStatePublisher>? _logger;
    private readonly object _throttleGate = new();
    private DateTimeOffset _lastTimelineBroadcast = DateTimeOffset.MinValue;
    private bool _started;
    private bool _disposed;

    public MediaLinkStatePublisher(
        MediaSourceCoordinator coordinator,
        MediaLinkSessionHub hub,
        Func<int>? timelineMinIntervalMs = null,
        Func<DateTimeOffset>? utcNow = null,
        ILogger<MediaLinkStatePublisher>? logger = null)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _timelineMinIntervalMs = timelineMinIntervalMs ?? (() => 200);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _logger = logger;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            return;
        }

        _coordinator.EffectiveMediaChanged += OnEffectiveMediaChanged;
        _coordinator.EffectiveLyricsChanged += OnEffectiveLyricsChanged;
        _started = true;
    }

    public void Stop()
    {
        if (!_started)
        {
            return;
        }

        _coordinator.EffectiveMediaChanged -= OnEffectiveMediaChanged;
        _coordinator.EffectiveLyricsChanged -= OnEffectiveLyricsChanged;
        _started = false;
    }

    public async Task PublishSnapshotAsync(MediaLinkSession session, CancellationToken cancellationToken = default)
    {
        var media = _coordinator.GetMediaForPush();
        if (session.IsSubscribedTo(MediaLinkProtocol.ChannelMedia))
        {
            await session.SendEventAsync(
                MediaLinkProtocol.EventMediaUpdated,
                MediaLinkDtoMapper.ToMediaDto(media, MediaInfoChangeKind.CurrentSession),
                cancellationToken);
        }

        if (session.IsSubscribedTo(MediaLinkProtocol.ChannelLyrics))
        {
            var lyrics = _coordinator.GetLyricsForPush();
            await session.SendEventAsync(
                MediaLinkProtocol.EventLyricsUpdated,
                MediaLinkDtoMapper.ToLyricsDto(lyrics),
                cancellationToken);
        }
    }

    private void OnEffectiveMediaChanged(object? sender, MediaInfoChangedEventArgs e)
    {
        if (e.ChangeKind == MediaInfoChangeKind.Timeline && !ShouldBroadcastTimeline())
        {
            return;
        }

        if (e.ChangeKind != MediaInfoChangeKind.Timeline)
        {
            lock (_throttleGate)
            {
                _lastTimelineBroadcast = DateTimeOffset.MinValue;
            }
        }

        // Always re-read push view so PushUsesEffective is honored at send time.
        var media = _coordinator.GetMediaForPush();
        var dto = MediaLinkDtoMapper.ToMediaDto(media, e.ChangeKind);
        _ = SafeBroadcastAsync(MediaLinkProtocol.ChannelMedia, MediaLinkProtocol.EventMediaUpdated, dto);
    }

    private void OnEffectiveLyricsChanged(object? sender, LyricsSearchResultChangedEventArgs e)
    {
        var lyrics = _coordinator.GetLyricsForPush();
        var dto = MediaLinkDtoMapper.ToLyricsDto(lyrics);
        _ = SafeBroadcastAsync(MediaLinkProtocol.ChannelLyrics, MediaLinkProtocol.EventLyricsUpdated, dto);
    }

    private bool ShouldBroadcastTimeline()
    {
        var now = _utcNow();
        var minInterval = Math.Max(0, _timelineMinIntervalMs());
        lock (_throttleGate)
        {
            if ((now - _lastTimelineBroadcast).TotalMilliseconds < minInterval)
            {
                return false;
            }

            _lastTimelineBroadcast = now;
            return true;
        }
    }

    private async Task SafeBroadcastAsync(string channel, string eventName, object? payload)
    {
        try
        {
            await _hub.BroadcastEventAsync(channel, eventName, payload);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "MediaLink broadcast failed for {EventName}.", eventName);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
    }
}
