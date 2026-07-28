namespace MediaIsland.Services.Media.Platform.Windows;

public sealed class WindowsMediaPlatformProvider(
    WindowsSmtcMediaSessionProvider sessionProvider,
    WindowsMediaSourceInfoProvider sourceInfoProvider,
    WindowsSmtcMediaPlaybackController playbackController) : IMediaPlatformProvider
{
    public string Id => "windows";

    public bool IsSupported => OperatingSystem.IsWindows();

    public int Priority => 100;

    public IMediaSessionProvider SessionProvider => sessionProvider;

    public IMediaSourceInfoProvider SourceInfoProvider => sourceInfoProvider;

    public IMediaPlaybackController? PlaybackController => playbackController;
}
