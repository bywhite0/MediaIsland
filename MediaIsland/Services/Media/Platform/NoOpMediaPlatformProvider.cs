namespace MediaIsland.Services.Media.Platform;

public sealed class NoOpMediaPlatformProvider : IMediaPlatformProvider
{
    public string Id => "noop";

    public bool IsSupported => true;

    public int Priority => int.MinValue;

    public IMediaSessionProvider SessionProvider { get; } = new NoOpMediaSessionProvider();

    public IMediaSourceInfoProvider SourceInfoProvider { get; } = new NoOpMediaSourceInfoProvider();
}

public sealed class NoOpMediaSessionProvider : IMediaSessionProvider
{
    public event EventHandler<MediaSessionSnapshotEventArgs>? FocusedSessionChanged;

    public event EventHandler<MediaSessionSnapshotEventArgs>? MediaPropertiesChanged
    {
        add { }
        remove { }
    }

    public event EventHandler<MediaSessionSnapshotEventArgs>? PlaybackChanged
    {
        add { }
        remove { }
    }

    public event EventHandler<MediaSessionSnapshotEventArgs>? TimelineChanged
    {
        add { }
        remove { }
    }

    public bool IsStarted { get; private set; }

    public MediaSessionSnapshot? CurrentSnapshot => null;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        IsStarted = true;
        FocusedSessionChanged?.Invoke(this, new MediaSessionSnapshotEventArgs(null));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        IsStarted = false;
        return Task.CompletedTask;
    }

    public Task<MediaSessionSnapshot?> GetCurrentSnapshotAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<MediaSessionSnapshot?>(null);
    }

    public ValueTask DisposeAsync()
    {
        FocusedSessionChanged = null;
        return ValueTask.CompletedTask;
    }
}

public sealed class NoOpMediaSourceInfoProvider : IMediaSourceInfoProvider
{
    public Task<MediaSourceInfo?> ResolveAsync(string sourceApp, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<MediaSourceInfo?>(null);
    }
}
