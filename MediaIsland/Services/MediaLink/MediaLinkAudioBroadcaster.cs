using MediaIsland.Services.Audio;
using MediaIsland.Services.Media;
using MediaIsland.Services.MediaLink.Mapping;
using MediaIsland.Services.MediaLink.Protocol;
using Microsoft.Extensions.Logging;

namespace MediaIsland.Services.MediaLink;

/// <summary>
/// 把采集到的 PCM 盖上曲目标识与时间戳，编码为协议帧并广播给订阅 <c>audio</c> 的会话。
///
/// 这是 <c>Services/Audio</c> 与 <c>Services/MediaLink</c> 的唯一接缝：它同时知道
/// 曲目（问 <see cref="MediaSourceCoordinator"/>）与协议（调 <see cref="MediaLinkAudioFrame"/>），
/// 而那两层都不知道对方存在。
/// </summary>
internal sealed class MediaLinkAudioBroadcaster : IAudioFrameSink
{
    private readonly MediaLinkSessionHub _hub;
    private readonly Func<MediaInfo?> _mediaAccessor;
    private readonly Func<long> _nowUnixMs;
    private readonly Func<long> _nowQpc100Ns;
    private readonly ILogger? _logger;

    private uint _seq;
    private string? _lastTrackToken;

    public MediaLinkAudioBroadcaster(
        MediaLinkSessionHub hub,
        Func<MediaInfo?> mediaAccessor,
        Func<long>? nowUnixMs = null,
        Func<long>? nowQpc100Ns = null,
        ILogger? logger = null)
    {
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _mediaAccessor = mediaAccessor ?? throw new ArgumentNullException(nameof(mediaAccessor));
        _nowUnixMs = nowUnixMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _nowQpc100Ns = nowQpc100Ns ?? MonotonicClock.Now100Ns;
        _logger = logger;
    }

    /// <summary>累计因无当前曲目而丢弃的帧数。用于诊断，不参与协议行为。</summary>
    public long DroppedWithoutTrack { get; private set; }

    public async ValueTask OnFrameAsync(AudioFrame frame, CancellationToken cancellationToken)
    {
        var media = _mediaAccessor();
        if (media is null)
        {
            // 没有曲目就没有时间轴，发出去的帧对不上任何东西。
            DroppedWithoutTrack++;
            return;
        }

        var trackToken = MediaLinkDtoMapper.ComputeTrackToken(
            media.SourceApp, media.Title, media.Artist, media.AlbumTitle);

        var nowMs = _nowUnixMs();
        var ageMs = Math.Max(0, (_nowQpc100Ns() - frame.QpcPosition100Ns) / MonotonicClock.TicksPerMs100Ns);
        var capturedAtMs = nowMs - ageMs;

        // 位置是「现在」的读数，而本帧采于 ageMs 之前，故往回拨。
        // 暂停时曲目位置不随时间前进，回拨会得到一个曲目上并不存在的位置。
        var positionMs = (long)media.Position.TotalMilliseconds;
        var startPositionMs = media.PlaybackInfo.PlaybackState == MediaPlaybackState.Playing
            ? Math.Max(0, positionMs - ageMs)
            : Math.Max(0, positionMs);

        var flags = MediaLinkAudioFrameFlags.None;
        if (frame.IsSilent)
        {
            flags |= MediaLinkAudioFrameFlags.Silent;
        }

        if (!string.Equals(_lastTrackToken, trackToken, StringComparison.Ordinal))
        {
            flags |= MediaLinkAudioFrameFlags.TrackStart;
            _lastTrackToken = trackToken;
        }

        var header = new MediaLinkAudioFrameHeader(
            StartPositionMs: startPositionMs,
            CapturedAtMs: capturedAtMs,
            ServerTimeMs: nowMs,
            Seq: unchecked(_seq++),
            TrackToken: trackToken,
            Flags: flags);

        var encoded = MediaLinkAudioFrame.Encode(in header, frame.Pcm);

        try
        {
            await _hub.BroadcastAudioFrameAsync(encoded, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[音频] 广播音频帧失败");
        }
    }
}
