using Microsoft.Extensions.Logging;

namespace MediaIsland.Services.Media.Platform;

public sealed class MediaPlatformProviderResolver(
    IEnumerable<IMediaPlatformProvider> providers,
    ILogger<MediaPlatformProviderResolver> logger)
{
    private IMediaPlatformProvider? _resolvedProvider;

    public IMediaPlatformProvider Resolve()
    {
        if (_resolvedProvider != null)
        {
            return _resolvedProvider;
        }

        _resolvedProvider = providers
            .Where(provider => provider.IsSupported)
            .OrderByDescending(provider => provider.Priority)
            .First();

        logger.LogInformation("Selected media platform provider: {ProviderId}", _resolvedProvider.Id);
        return _resolvedProvider;
    }
}
