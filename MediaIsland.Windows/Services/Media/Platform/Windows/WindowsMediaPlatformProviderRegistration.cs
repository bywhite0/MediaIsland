using Microsoft.Extensions.DependencyInjection;

namespace MediaIsland.Services.Media.Platform.Windows;

public static class WindowsMediaPlatformProviderRegistration
{
    public static IServiceCollection AddWindowsMediaPlatformProvider(IServiceCollection services)
    {
        services.AddSingleton<WindowsSmtcMediaSessionProvider>();
        services.AddSingleton<IMediaPlatformProvider, WindowsMediaPlatformProvider>();
        return services;
    }
}
