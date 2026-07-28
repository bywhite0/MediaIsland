using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Media;
using MediaIsland.Services.Realtime.Mapping;
using MediaIsland.Services.Realtime.Protocol;
using Microsoft.Extensions.Logging;

namespace MediaIsland.Services.Realtime;

public sealed class RealtimeStatePublisher : IDisposable
{
    private readonly IMediaService _mediaService;
    private readonly LyricsSearchService _lyricsSearchService;
    private readonly RealtimeSessionHub _hub;
    private readonly Func<int> _timelineMinIntervalMs;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly ILogger<RealtimeStatePublisher>? _logger;
    private readonly object _throttleGate = new();
    private DateTimeOffset _lastTimelineBroadcast = DateTimeOffset.MinValue;
    private bool _started;
    private bool _disposed;

    public RealtimeStatePublisher(
        IMediaService mediaService,
        LyricsSearchService lyricsSearchService,
        RealtimeSessionHub hub,
        Func<int>? timelineMinIntervalMs = null,
        Func<DateTimeOffset>? utcNow = null,
        ILogger<RealtimeStatePublisher>? logger = null)
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

    public async Task PublishSnapshotAsync(RealtimeSession session, CancellationToken cancellationToken = default)
    {
        var media = _mediaService.CurrentMediaInfo;
        if (session.IsSubscribedTo(RealtimeProtocol.ChannelMedia))
        {
            await session.SendEventAsync(
                RealtimeProtocol.EventMediaUpdated,
                RealtimeDtoMapper.ToMediaDto(media, MediaInfoChangeKind.CurrentSession),
                cancellationToken);
        }

        if (session.IsSubscribedTo(RealtimeProtocol.ChannelLyrics))
        {
            var lyrics = _lyricsSearchService.GetCurrentResultFor(media) ?? _lyricsSearchService.CurrentResult;
            await session.SendEventAsync(
                RealtimeProtocol.EventLyricsUpdated,
                RealtimeDtoMapper.ToLyricsDto(lyrics),
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

        var dto = RealtimeDtoMapper.ToMediaDto(e.MediaInfo, e.ChangeKind);
        _ = SafeBroadcastAsync(RealtimeProtocol.ChannelMedia, RealtimeProtocol.EventMediaUpdated, dto);
    }

    private void OnLyricsChanged(object? sender, LyricsSearchResultChangedEventArgs e)
    {
        var dto = RealtimeDtoMapper.ToLyricsDto(e.Result);
        _ = SafeBroadcastAsync(RealtimeProtocol.ChannelLyrics, RealtimeProtocol.EventLyricsUpdated, dto);
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
            _logger?.LogDebug(ex, "Realtime broadcast failed for {EventName}.", eventName);
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
