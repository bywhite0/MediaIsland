using Avalonia.Media.Imaging;
using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Lyrics.Models;
using MediaIsland.Services.Media;
using MediaIsland.Services.MediaLink.Mapping;
using MediaIsland.Services.MediaLink.Protocol;

namespace MediaIsland.Services.MediaLink;

public sealed class MediaLinkInjectionStore
{
    private readonly object _gate = new();
    private readonly Func<long> _tickProvider;

    private string _sourceApp = "external";
    private string? _title;
    private string? _artist;
    private string? _albumTitle;
    private TimeSpan _duration;
    private MediaPlaybackState _playbackState = MediaPlaybackState.Paused;
    private double _playbackRate = 1.0;
    private bool _hasMedia;

    private TimeSpan _basePosition;
    private long _baseTick;
    private bool _isPlaying;

    private LyricsSearchResult? _lyrics;
    private DateTimeOffset? _lastUpdatedUtc;

    /// <summary>
    /// 当前曲目的封面，以及封面已解析到哪一首。两者必须一起换：
    /// 只存图会让切歌后旧封面配到新曲目上，而封面与媒体元数据是分两条消息到达的
    /// ——<c>media.updated</c> 先到，封面要再问一次才回来。
    ///
    /// token 记「已解析过」而非「有图」：确认无封面的曲目同样记下，
    /// 使重复的空回复能被识别为无变化，不必每条都惊动 UI 重刷一遍。
    /// </summary>
    private Bitmap? _thumbnail;
    private string? _thumbnailToken;

    public MediaLinkInjectionStore(Func<long>? tickProvider = null)
    {
        _tickProvider = tickProvider ?? (() => Environment.TickCount64);
    }

    public bool HasExternalMedia
    {
        get
        {
            lock (_gate)
            {
                return _hasMedia;
            }
        }
    }

    public bool HasExternalLyrics
    {
        get
        {
            lock (_gate)
            {
                return _lyrics is not null;
            }
        }
    }

    public DateTimeOffset? LastUpdatedUtc
    {
        get
        {
            lock (_gate)
            {
                return _lastUpdatedUtc;
            }
        }
    }

    public event EventHandler<MediaLinkInjectionChangedEventArgs>? Changed;

    /// <param name="changeKind">
    /// 本次写入代表哪一类变更。默认 <see cref="MediaInfoChangeKind.CurrentSession"/>，
    /// 即「当作换了会话」——注入方没有更好的信息时的保守侧。
    ///
    /// 转发链上的接收侧必须显式传入上游告知的种类：连续的位置更新若以默认值写入，
    /// 下游会把每一条都当成换歌。
    /// </param>
    public bool TrySetMedia(
        MediaLinkMediaInjectPayload payload,
        out string? error,
        MediaInfoChangeKind changeKind = MediaInfoChangeKind.CurrentSession)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (string.IsNullOrWhiteSpace(payload.Title))
        {
            error = "title is required";
            return false;
        }

        if (payload.PositionMs < 0 || payload.DurationMs < 0)
        {
            error = "positionMs and durationMs must be >= 0";
            return false;
        }

        if (string.IsNullOrWhiteSpace(payload.PlaybackState) ||
            !Enum.TryParse<MediaPlaybackState>(payload.PlaybackState, ignoreCase: true, out var state))
        {
            error = "playbackState is invalid";
            return false;
        }

        if (payload.PositionAgeMs is < 0)
        {
            error = "positionAgeMs must be >= 0";
            return false;
        }

        var rate = payload.PlaybackRate is > 0 ? payload.PlaybackRate.Value : 1.0;
        // 与 ComputeTrackToken 共用同一个规范化入口。两处各写一遍时，
        // 任一侧规则变动都会让 token 在链路两端分裂。
        var identity = MediaLinkTrackIdentity.Normalize(
            payload.SourceApp, payload.Title, payload.Artist, payload.AlbumTitle);
        var sourceApp = identity.SourceApp;
        var title = identity.Title!;   // 上面已校验 Title 非空白，故此处必不为 null
        var artist = identity.Artist;
        var albumTitle = identity.AlbumTitle;
        var position = TimeSpan.FromMilliseconds(payload.PositionMs);
        var duration = TimeSpan.FromMilliseconds(payload.DurationMs);

        // 把基准时刻回拨 positionAgeMs：位置值对应的是过去那一刻，不是现在。
        // 不回拨的话，采样与注入之间的时间被静默丢弃，转发链上每跳都落后一次。
        var nowTick = _tickProvider() - (payload.PositionAgeMs ?? 0);

        // 在锁外先算：SHA256 与锁无关，放进去只是白占临界区。
        var trackToken = MediaLinkDtoMapper.ComputeTrackToken(sourceApp, title, artist, albumTitle);

        lock (_gate)
        {
            // 换歌即作废旧封面。封面与元数据分两条消息到达，新曲目的图要再问一次才回来；
            // 不作废则这段空窗里新曲目顶着上一首的封面，切歌快时用户看到的封面一直落后一首。
            if (!string.Equals(_thumbnailToken, trackToken, StringComparison.Ordinal))
            {
                ClearThumbnailLocked();
            }

            _sourceApp = sourceApp;
            _title = title;
            _artist = artist;
            _albumTitle = albumTitle;
            _duration = duration;
            _playbackState = state;
            _playbackRate = rate;
            _basePosition = position;
            _baseTick = nowTick;
            _isPlaying = state == MediaPlaybackState.Playing;
            _hasMedia = true;
            _lastUpdatedUtc = DateTimeOffset.UtcNow;
        }

        error = null;
        RaiseChanged(changeKind);
        return true;
    }

    public bool TrySetLyrics(MediaLinkLyricsDto payload, out string? error)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (!MediaLinkDtoMapper.TryMapInjectedLyrics(payload, out var result, out error))
        {
            return false;
        }

        lock (_gate)
        {
            _lyrics = result;
            _lastUpdatedUtc = DateTimeOffset.UtcNow;
        }

        error = null;
        // 歌词内容本身换了，必须走整条重载路径。
        RaiseChanged(MediaInfoChangeKind.CurrentSession);
        return true;
    }

    /// <summary>
    /// 写入封面。<paramref name="trackToken"/> 必须与当前曲目一致，否则丢弃——
    /// 封面是异步问来的，切歌后迟到的那份属于上一首，装上去就是配错图。
    /// </summary>
    /// <returns>是否被接受。失配与无媒体都返回 false，两者都不是错误。</returns>
    public bool TrySetThumbnail(string? trackToken, Bitmap? thumbnail)
    {
        if (string.IsNullOrEmpty(trackToken))
        {
            return false;
        }

        lock (_gate)
        {
            if (!_hasMedia || !string.Equals(ComputeTokenLocked(), trackToken, StringComparison.Ordinal))
            {
                return false;
            }

            // 同一结果重复写入是空操作：下面要发 Changed，而那会驱动组件重刷一遍 UI。
            // 「同一结果」含两次都是空：上游对无封面曲目的重复回复不该反复刷屏。
            if (ReferenceEquals(_thumbnail, thumbnail) &&
                string.Equals(_thumbnailToken, trackToken, StringComparison.Ordinal))
            {
                return true;
            }

            _thumbnail = thumbnail;
            _thumbnailToken = trackToken;
            _lastUpdatedUtc = DateTimeOffset.UtcNow;
        }

        // 封面属于媒体属性，不是换会话。报成 CurrentSession 会让歌词组件走整条重载路径。
        RaiseChanged(MediaInfoChangeKind.MediaProperties);
        return true;
    }

    public bool TryClear(IReadOnlyList<string>? channels, out string? error)
    {
        var clearMedia = false;
        var clearLyrics = false;

        if (channels is null || channels.Count == 0)
        {
            clearMedia = true;
            clearLyrics = true;
        }
        else
        {
            foreach (var channel in channels)
            {
                if (string.Equals(channel, MediaLinkProtocol.ChannelMedia, StringComparison.Ordinal))
                {
                    clearMedia = true;
                    continue;
                }

                if (string.Equals(channel, MediaLinkProtocol.ChannelLyrics, StringComparison.Ordinal))
                {
                    clearLyrics = true;
                    continue;
                }

                error = $"unknown channel: {channel}";
                return false;
            }
        }

        var changed = false;
        lock (_gate)
        {
            if (clearMedia && _hasMedia)
            {
                ClearMediaLocked();
                changed = true;
            }

            if (clearLyrics && _lyrics is not null)
            {
                _lyrics = null;
                changed = true;
            }

            if (changed)
            {
                _lastUpdatedUtc = DateTimeOffset.UtcNow;
            }
        }

        error = null;
        if (changed)
        {
            RaiseChanged(MediaInfoChangeKind.CurrentSession);
        }

        return true;
    }

    public MediaInfo? GetMediaSnapshot()
    {
        lock (_gate)
        {
            if (!_hasMedia)
            {
                return null;
            }

            var position = GetPositionLocked();
            return new MediaInfo(
                _sourceApp,
                _title,
                _artist,
                _albumTitle,
                position,
                _duration,
                new MediaPlaybackInfo(_playbackState, _playbackRate),
                // 不变量已由 TrySetMedia 与 TrySetThumbnail 守住：_thumbnail 非空即属于当前曲目，
                // 故此处无需再比一次 token。
                Thumbnail: _thumbnail,
                // 保持 null：拿到的已是解码好的位图，没有可延迟加载的源。
                // 组件里那条 ThumbnailSource 分支是给 Spotify 裁标用的，而注入的图
                // 在上游就已按上游的设置裁过，本机再裁一次是错的。
                ThumbnailSource: null);
        }
    }

    public LyricsSearchResult? GetLyricsSnapshot()
    {
        lock (_gate)
        {
            return _lyrics;
        }
    }

    public bool TryVirtualPlay(out string? error)
    {
        lock (_gate)
        {
            if (!_hasMedia)
            {
                error = "no external media";
                return false;
            }

            if (_isPlaying && _playbackState == MediaPlaybackState.Playing)
            {
                error = null;
                return true;
            }

            var position = GetPositionLocked();
            _basePosition = position;
            _baseTick = _tickProvider();
            _isPlaying = true;
            _playbackState = MediaPlaybackState.Playing;
            _lastUpdatedUtc = DateTimeOffset.UtcNow;
        }

        error = null;
        RaiseChanged(MediaInfoChangeKind.CurrentSession);
        return true;
    }

    public bool TryVirtualPause(out string? error)
    {
        lock (_gate)
        {
            if (!_hasMedia)
            {
                error = "no external media";
                return false;
            }

            if (!_isPlaying && _playbackState == MediaPlaybackState.Paused)
            {
                error = null;
                return true;
            }

            var position = GetPositionLocked();
            _basePosition = position;
            _baseTick = _tickProvider();
            _isPlaying = false;
            _playbackState = MediaPlaybackState.Paused;
            _lastUpdatedUtc = DateTimeOffset.UtcNow;
        }

        error = null;
        RaiseChanged(MediaInfoChangeKind.CurrentSession);
        return true;
    }

    private TimeSpan GetPositionLocked()
    {
        if (!_isPlaying)
        {
            return _basePosition;
        }

        var elapsedMs = (_tickProvider() - _baseTick) * _playbackRate;
        var position = _basePosition + TimeSpan.FromMilliseconds(elapsedMs);
        if (_duration > TimeSpan.Zero && position > _duration)
        {
            return _duration;
        }

        if (position < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return position;
    }

    private void ClearMediaLocked()
    {
        _hasMedia = false;
        _sourceApp = "external";
        _title = null;
        _artist = null;
        _albumTitle = null;
        _duration = TimeSpan.Zero;
        _playbackState = MediaPlaybackState.Paused;
        _playbackRate = 1.0;
        _basePosition = TimeSpan.Zero;
        _baseTick = _tickProvider();
        _isPlaying = false;
        ClearThumbnailLocked();
    }

    /// <summary>
    /// 丢引用而不 Dispose。这张位图可能正挂在某个 Image 上，释放已显示的图会抛异常
    /// 或画出花屏；交给 GC 是平台侧那条路径的既有做法，两处保持一致。
    /// </summary>
    private void ClearThumbnailLocked()
    {
        _thumbnail = null;
        _thumbnailToken = null;
    }

    /// <summary>
    /// 封面已解析到哪一首，未解析为 null。只读视图，供测试断言
    /// 「切歌即作废」这条不变量——它的正确输出是一张位图，
    /// 而位图需要渲染后端，测试进程里造不出来。
    /// </summary>
    internal string? ResolvedThumbnailToken
    {
        get
        {
            lock (_gate)
            {
                return _thumbnailToken;
            }
        }
    }

    /// <summary>当前曲目的 token。与发送侧同源计算，故跨机比对必然一致。</summary>
    private string ComputeTokenLocked() =>
        MediaLinkDtoMapper.ComputeTrackToken(_sourceApp, _title, _artist, _albumTitle);

    private void RaiseChanged(MediaInfoChangeKind changeKind)
    {
        Changed?.Invoke(this, new MediaLinkInjectionChangedEventArgs(changeKind));
    }
}

/// <summary>
/// 一次注入写入所代表的变更种类。它必须随写入一起传出去，不能由订阅方推断——
/// 只有写入方知道这是换歌还是仅仅位置前进，而两者在存储的最终状态上无从区分。
/// </summary>
public sealed class MediaLinkInjectionChangedEventArgs(MediaInfoChangeKind changeKind) : EventArgs
{
    public MediaInfoChangeKind ChangeKind { get; } = changeKind;
}
