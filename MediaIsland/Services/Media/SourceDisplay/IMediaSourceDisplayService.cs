using Avalonia.Media.Imaging;

namespace MediaIsland.Services.Media.SourceDisplay;

public interface IMediaSourceDisplayService
{
    Task<MediaSourceDisplayInfo> ResolveAsync(string sourceApp, CancellationToken cancellationToken = default);

    void Invalidate(string sourceApp);
}

public enum MediaSourceDisplayKind
{
    Unknown,
    Mapping,
    Bundled,
    Platform,
    UserConfigured
}

public sealed record MediaSourceDisplayInfo(
    string SourceApp,
    string DisplayName,
    Bitmap? Icon,
    MediaSourceDisplayKind Kind);
