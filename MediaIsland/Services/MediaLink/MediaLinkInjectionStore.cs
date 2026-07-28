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

    public event EventHandler? Changed;

    public bool TrySetMedia(MediaLinkMediaInjectPayload payload, out string? error)
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

        var rate = payload.PlaybackRate is > 0 ? payload.PlaybackRate.Value : 1.0;
        var sourceApp = string.IsNullOrWhiteSpace(payload.SourceApp)
            ? "external"
            : payload.SourceApp.Trim();
        var title = payload.Title.Trim();
        var artist = string.IsNullOrWhiteSpace(payload.Artist) ? null : payload.Artist.Trim();
        var albumTitle = string.IsNullOrWhiteSpace(payload.AlbumTitle) ? null : payload.AlbumTitle.Trim();
        var position = TimeSpan.FromMilliseconds(payload.PositionMs);
        var duration = TimeSpan.FromMilliseconds(payload.DurationMs);
        var nowTick = _tickProvider();

        lock (_gate)
        {
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
        RaiseChanged();
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
        RaiseChanged();
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
            RaiseChanged();
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
                Thumbnail: null,
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
        RaiseChanged();
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
        RaiseChanged();
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
    }

    private void RaiseChanged()
    {
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
