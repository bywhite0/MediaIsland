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
    private long _seq;
    private DateTimeOffset _windowStart = DateTimeOffset.MinValue;
    private MediaLinkMediaDto? _pendingTimeline;
    private CancellationTokenSource? _trailingCts;
    private Task? _trailingTask;
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
        CancelTrailing();
        _started = false;
    }

    public async Task PublishSnapshotAsync(MediaLinkSession session, CancellationToken cancellationToken = default)
    {
        var media = _coordinator.GetMediaForPush();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // 快照与广播共用 seq 分配器，故也必须经同一道门：否则新订阅者可能先收到
        // 增量、后收到序号更小的快照，并永久停在旧状态（D2）。
        await _broadcastGate.WaitAsync(cancellationToken);
        try
        {
            if (session.IsSubscribedTo(MediaLinkProtocol.ChannelMedia))
            {
                var dto = MediaLinkDtoMapper.ToMediaDto(media, MediaInfoChangeKind.CurrentSession);
                if (dto is not null)
                {
                    dto.PositionCapturedAtMs = nowMs;
                    dto.ServerTimeMs = nowMs;
                }
                await session.EnqueueAsync(
                    MediaLinkMessageSerializer.Create(
                        MediaLinkProtocol.TypeEvent, dto,
                        name: MediaLinkProtocol.EventMediaUpdated,
                        ts: nowMs, seq: NextSeq()),
                    droppable: true,
                    cancellationToken);
            }

            if (session.IsSubscribedTo(MediaLinkProtocol.ChannelLyrics))
            {
                var lyrics = _coordinator.GetLyricsForPush();
                await session.EnqueueAsync(
                    MediaLinkMessageSerializer.Create(
                        MediaLinkProtocol.TypeEvent,
                        MediaLinkDtoMapper.ToLyricsDto(lyrics, media),
                        name: MediaLinkProtocol.EventLyricsUpdated,
                        ts: nowMs, seq: NextSeq()),
                    droppable: false,
                    cancellationToken);
            }
        }
        finally
        {
            _broadcastGate.Release();
        }
    }

    private long NextSeq() => Interlocked.Increment(ref _seq);

    private void OnEffectiveMediaChanged(object? sender, MediaInfoChangedEventArgs e)
    {
        if (e.ChangeKind == MediaInfoChangeKind.Timeline)
        {
            HandleTimelineThrottled();
            return;
        }

        // Non-timeline: send immediately, reset throttle window.
        CancelTrailing();
        lock (_throttleGate)
        {
            _windowStart = DateTimeOffset.MinValue;
            _pendingTimeline = null;
        }

        var media = _coordinator.GetMediaForPush();
        var dto = MediaLinkDtoMapper.ToMediaDto(media, e.ChangeKind);
        if (dto is not null)
        {
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            dto.PositionCapturedAtMs = nowMs;
            dto.ServerTimeMs = nowMs;
        }
        _ = SafeBroadcastMediaAsync(dto);
    }

    private void HandleTimelineThrottled()
    {
        var now = _utcNow();
        var minInterval = Math.Max(0, _timelineMinIntervalMs());
        MediaLinkMediaDto? toSendNow = null;

        lock (_throttleGate)
        {
            var elapsed = (now - _windowStart).TotalMilliseconds;
            if (elapsed >= minInterval)
            {
                // Leading edge: send immediately, start window.
                _windowStart = now;
                _pendingTimeline = null;
                var media = _coordinator.GetMediaForPush();
                toSendNow = MediaLinkDtoMapper.ToMediaDto(media, MediaInfoChangeKind.Timeline);
                if (toSendNow is not null)
                {
                    var nowMs = now.ToUnixTimeMilliseconds();
                    toSendNow.PositionCapturedAtMs = nowMs;
                    toSendNow.ServerTimeMs = nowMs;
                }
                ScheduleTrailing(minInterval);
            }
            else
            {
                // In window: store latest, trailing edge will send it.
                var media = _coordinator.GetMediaForPush();
                _pendingTimeline = MediaLinkDtoMapper.ToMediaDto(media, MediaInfoChangeKind.Timeline);
                if (_pendingTimeline is not null)
                {
                    var nowMs = now.ToUnixTimeMilliseconds();
                    _pendingTimeline.PositionCapturedAtMs = nowMs;
                }
            }
        }

        if (toSendNow is not null)
        {
            _ = SafeBroadcastMediaAsync(toSendNow);
        }
    }

    private void ScheduleTrailing(int delayMs)
    {
        CancelTrailing();
        var cts = new CancellationTokenSource();
        _trailingCts = cts;
        _trailingTask = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs, cts.Token);
                MediaLinkMediaDto? pending;
                lock (_throttleGate)
                {
                    pending = _pendingTimeline;
                    _pendingTimeline = null;
                    _windowStart = DateTimeOffset.MinValue;
                }
                if (pending is not null && !cts.Token.IsCancellationRequested)
                {
                    var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    pending.ServerTimeMs = nowMs;
                    await SafeBroadcastMediaAsync(pending);
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    private void CancelTrailing()
    {
        _trailingCts?.Cancel();
        _trailingCts?.Dispose();
        _trailingCts = null;
        _trailingTask = null;
    }

    private void OnEffectiveLyricsChanged(object? sender, LyricsSearchResultChangedEventArgs e)
    {
        var lyrics = _coordinator.GetLyricsForPush();
        // 歌词必须携带其所属媒体的 trackToken，客户端据此丢弃切歌后到达的陈旧歌词。
        var dto = MediaLinkDtoMapper.ToLyricsDto(lyrics, _coordinator.GetMediaForPush());
        _ = SafeBroadcastLyricsAsync(dto);
    }

    /// <summary>
    /// seq 分配必须与入队原子完成。分配与入队之间若可交错，两个并发变更能拿到
    /// seq=5 / seq=6 后以相反顺序入队，客户端按"丢弃 seq ≤ 已处理值"就会丢掉较新的那条。
    /// 广播本身只是入队（不等 socket），故串行化不会让慢客户端阻塞发布。
    /// </summary>
    private readonly SemaphoreSlim _broadcastGate = new(1, 1);

    private async Task SafeBroadcastMediaAsync(MediaLinkMediaDto? dto)
    {
        try
        {
            await _broadcastGate.WaitAsync();
            try
            {
                await _hub.BroadcastMediaUpdatedAsync(dto, NextSeq());
            }
            finally
            {
                _broadcastGate.Release();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "MediaLink media broadcast failed.");
        }
    }

    private async Task SafeBroadcastLyricsAsync(MediaLinkLyricsDto? dto)
    {
        try
        {
            await _broadcastGate.WaitAsync();
            try
            {
                await _hub.BroadcastLyricsUpdatedAsync(dto, NextSeq());
            }
            finally
            {
                _broadcastGate.Release();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "MediaLink lyrics broadcast failed.");
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
        _broadcastGate.Dispose();
    }
}