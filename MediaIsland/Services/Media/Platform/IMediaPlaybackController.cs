namespace MediaIsland.Services.Media.Platform;

public interface IMediaPlaybackController
{
    MediaPlaybackCapabilities Capabilities { get; }

    Task<MediaPlaybackCommandResult> ExecuteAsync(
        MediaPlaybackCommand command,
        CancellationToken cancellationToken = default);
}

public enum MediaPlaybackCommand
{
    Play,
    Pause,
    Next,
    Previous
}

[Flags]
public enum MediaPlaybackCapabilities
{
    None = 0,
    Play = 1,
    Pause = 2,
    Next = 4,
    Previous = 8
}

public enum MediaPlaybackCommandStatus
{
    Succeeded,
    NotSupported,
    NoSession,
    Failed
}

public readonly record struct MediaPlaybackCommandResult(
    MediaPlaybackCommandStatus Status,
    string? Message = null);
