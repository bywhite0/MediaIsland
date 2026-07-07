using Avalonia.Media.Imaging;

namespace MediaIsland.Services.Media.Platform;

public interface IMediaPlatformProvider
{
    string Id { get; }

    bool IsSupported { get; }

    int Priority { get; }

    IMediaSessionProvider SessionProvider { get; }

    IMediaSourceInfoProvider SourceInfoProvider { get; }
}

public interface IMediaSessionProvider : IAsyncDisposable
{
    event EventHandler<MediaSessionSnapshotEventArgs>? FocusedSessionChanged;

    event EventHandler<MediaSessionSnapshotEventArgs>? MediaPropertiesChanged;

    event EventHandler<MediaSessionSnapshotEventArgs>? PlaybackChanged;

    event EventHandler<MediaSessionSnapshotEventArgs>? TimelineChanged;

    bool IsStarted { get; }

    MediaSessionSnapshot? CurrentSnapshot { get; }

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    Task<MediaSessionSnapshot?> GetCurrentSnapshotAsync(CancellationToken cancellationToken = default);
}

public interface IMediaSourceInfoProvider
{
    Task<MediaSourceInfo?> ResolveAsync(string sourceApp, CancellationToken cancellationToken = default);
}

public sealed class MediaSessionSnapshotEventArgs(MediaSessionSnapshot? snapshot) : EventArgs
{
    public MediaSessionSnapshot? Snapshot { get; } = snapshot;
}

public enum MediaSourceInfoKind
{
    Unknown,
    Platform,
    UserConfigured
}

public sealed record MediaSourceInfo(
    string SourceApp,
    string? DisplayName,
    Bitmap? Icon,
    MediaSourceInfoKind Kind);
