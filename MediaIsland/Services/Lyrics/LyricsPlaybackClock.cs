using MediaIsland.Services.Media;

namespace MediaIsland.Services.Lyrics;

public sealed class LyricsPlaybackClock
{
    private readonly object _gate = new();
    private TimeSpan _basePosition;
    private long _baseTimestamp = Environment.TickCount64;
    private bool _isPlaying;
    private double _playbackRate = 1.0;

    public void Update(MediaInfo info)
    {
        lock (_gate)
        {
            _basePosition = info.Position;
            _baseTimestamp = Environment.TickCount64;
            _isPlaying = info.PlaybackInfo.PlaybackState == MediaPlaybackState.Playing;
            _playbackRate = info.PlaybackInfo.PlaybackRate is > 0 ? info.PlaybackInfo.PlaybackRate.Value : 1.0;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _basePosition = TimeSpan.Zero;
            _baseTimestamp = Environment.TickCount64;
            _isPlaying = false;
            _playbackRate = 1.0;
        }
    }

    public TimeSpan GetCurrentPosition()
    {
        lock (_gate)
        {
            if (!_isPlaying)
            {
                return _basePosition;
            }

            var elapsedMs = (Environment.TickCount64 - _baseTimestamp) * _playbackRate;
            return _basePosition + TimeSpan.FromMilliseconds(elapsedMs);
        }
    }

    public bool IsPlaying
    {
        get
        {
            lock (_gate)
            {
                return _isPlaying;
            }
        }
    }
}
