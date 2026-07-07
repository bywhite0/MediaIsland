using Avalonia.Media.Imaging;

namespace MediaIsland.Services.Media;

public enum MediaPlaybackState
{
    Unknown,
    Closed,
    Opened,
    Changing,
    Stopped,
    Playing,
    Paused
}

public enum MediaInfoChangeKind
{
    CurrentSession,
    MediaProperties,
    Playback,
    Timeline
}

public sealed record MediaPlaybackInfo(
    MediaPlaybackState PlaybackState,
    double? PlaybackRate = null);

public sealed record MediaThumbnail(
    Func<bool, CancellationToken, Task<Bitmap?>> LoadBitmapAsync);

public sealed record MediaSessionSnapshot(
    string SourceApp,
    string? Title,
    string? Artist,
    string? AlbumTitle,
    TimeSpan Position,
    TimeSpan Duration,
    MediaPlaybackInfo PlaybackInfo,
    MediaThumbnail? Thumbnail);

public sealed record MediaInfo(
    string SourceApp,
    string? Title,
    string? Artist,
    string? AlbumTitle,
    TimeSpan Position,
    TimeSpan Duration,
    MediaPlaybackInfo PlaybackInfo,
    Bitmap? Thumbnail,
    MediaThumbnail? ThumbnailSource);

public sealed class MediaInfoChangedEventArgs(MediaInfo? mediaInfo, MediaInfoChangeKind changeKind) : EventArgs
{
    public MediaInfo? MediaInfo { get; } = mediaInfo;

    public MediaInfoChangeKind ChangeKind { get; } = changeKind;
}
