using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Media;
using MediaIsland.Services.MediaLink.Mapping;
using MediaIsland.Services.MediaLink.Protocol;
using Microsoft.Extensions.Logging;

namespace MediaIsland.Services.MediaLink;

public sealed class MediaLinkStatePublisher : IDisposable
{
    private readonly IMediaService _mediaService;
    private readonly LyricsSearchService _lyricsSearchService;
    private readonly MediaLinkSessionHub _hub;
    private readonly Func<int> _timelineMinIntervalMs;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly ILogger<MediaLinkStatePublisher>? _logger;
    private readonly object _throttleGate = new();
    private DateTimeOffset _lastTimelineBroadcast = DateTimeOffset.MinValue;
    private bool _started;
    private bool _disposed;

    public MediaLinkStatePublisher(
        IMediaService mediaService,
        LyricsSearchService lyricsSearchService,
        MediaLinkSessionHub hub,
        Func<int>? timelineMinIntervalMs = null,
        Func<DateTimeOffset>? utcNow = null,
        ILogger<MediaLinkStatePublisher>? logger = null)
    {
        _mediaService = mediaService;
        _lyricsSearchService = lyricsSearchService;
        _hub = hub;
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

        _mediaService.MediaInfoChanged += OnMediaInfoChanged;
        _lyricsSearchService.CurrentResultChanged += OnLyricsChanged;
        _started = true;
    }

    public void Stop()
    {
        if (!_started)
        {
            return;
        }

        _mediaService.MediaInfoChanged -= OnMediaInfoChanged;
        _lyricsSearchService.CurrentResultChanged -= OnLyricsChanged;
        _started = false;
    }

    public async Task PublishSnapshotAsync(MediaLinkSession session, CancellationToken cancellationToken = default)
    {
        var media = _mediaService.CurrentMediaInfo;
        if (session.IsSubscribedTo(MediaLinkProtocol.ChannelMedia))
        {
            await session.SendEventAsync(
                MediaLinkProtocol.EventMediaUpdated,
                MediaLinkDtoMapper.ToMediaDto(media, MediaInfoChangeKind.CurrentSession),
                cancellationToken);
        }

        if (session.IsSubscribedTo(MediaLinkProtocol.ChannelLyrics))
        {
            var lyrics = _lyricsSearchService.GetCurrentResultFor(media) ?? _lyricsSearchService.CurrentResult;
            await session.SendEventAsync(
                MediaLinkProtocol.EventLyricsUpdated,
                MediaLinkDtoMapper.ToLyricsDto(lyrics),
                cancellationToken);
        }
    }

    private void OnMediaInfoChanged(object? sender, MediaInfoChangedEventArgs e)
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

        var dto = MediaLinkDtoMapper.ToMediaDto(e.MediaInfo, e.ChangeKind);
        _ = SafeBroadcastAsync(MediaLinkProtocol.ChannelMedia, MediaLinkProtocol.EventMediaUpdated, dto);
    }

    private void OnLyricsChanged(object? sender, LyricsSearchResultChangedEventArgs e)
    {
        var dto = MediaLinkDtoMapper.ToLyricsDto(e.Result);
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
