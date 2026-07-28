namespace MediaIsland.Services.Media.Platform.Windows;

public sealed class WindowsMediaPlatformProvider(
    WindowsSmtcMediaSessionProvider sessionProvider,
    WindowsMediaSourceInfoProvider sourceInfoProvider) : IMediaPlatformProvider
{
    public string Id => "windows";

    public bool IsSupported => OperatingSystem.IsWindows();

    public int Priority => 100;

    public IMediaSessionProvider SessionProvider => sessionProvider;

    public IMediaSourceInfoProvider SourceInfoProvider => sourceInfoProvider;

    // Placeholder until Task 3 wires WindowsSmtcMediaPlaybackController.
    public IMediaPlaybackController? PlaybackController => null;
}
