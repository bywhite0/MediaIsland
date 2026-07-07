using Microsoft.Extensions.Hosting;

namespace MediaIsland.Services.Media;

public interface IMediaService : IHostedService
{
    event EventHandler<MediaInfoChangedEventArgs>? MediaInfoChanged;

    MediaInfo? CurrentMediaInfo { get; }

    Task EnsureStartedAsync(CancellationToken cancellationToken = default);
}
