using MediaIsland.Models;
using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Media;
using MediaIsland.Services.Media.Platform;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MediaIsland.Services.MediaLink;

public sealed class MediaLinkHostedService : IHostedService, IMediaLinkGateway, IDisposable
{
    private static readonly TimeSpan DisposeStopTimeout = TimeSpan.FromSeconds(5);

    private readonly MediaLinkInjectionStore _injectionStore;
    private readonly MediaSourceCoordinator _coordinator;
    private readonly MediaPlatformProviderResolver _platformProviderResolver;
    private readonly Func<PluginSettings> _settingsFactory;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ILogger<MediaLinkHostedService>? _logger;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    private MediaLinkSessionHub? _hub;
    private MediaLinkStatePublisher? _publisher;
    private MediaLinkServer? _server;
    private PluginSettings? _boundSettings;
    private CancellationTokenSource? _debounceCts;
    private bool _disposed;

    public MediaLinkHostedService(
        IMediaService mediaService,
        LyricsSearchService lyricsSearchService,
        MediaLinkInjectionStore injectionStore,
        MediaSourceCoordinator coordinator,
        MediaPlatformProviderResolver platformProviderResolver,
        Func<PluginSettings> settingsFactory,
        ILoggerFactory? loggerFactory = null)
    {
        // mediaService/lyricsSearchService retained in signature for DI call sites / future use;
        // push path is exclusively via coordinator.
        _ = mediaService ?? throw new ArgumentNullException(nameof(mediaService));
        _ = lyricsSearchService ?? throw new ArgumentNullException(nameof(lyricsSearchService));
        _injectionStore = injectionStore ?? throw new ArgumentNullException(nameof(injectionStore));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _platformProviderResolver = platformProviderResolver
            ?? throw new ArgumentNullException(nameof(platformProviderResolver));
        _settingsFactory = settingsFactory ?? throw new ArgumentNullException(nameof(settingsFactory));
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<MediaLinkHostedService>();
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public bool IsRunning
    {
        get => _server?.IsRunning == true;
    }

    public string? Endpoint => _server?.Endpoint;

    public string? LastError { get; private set; }

    public int ActiveSessionCount => _hub?.Sessions.Count ?? 0;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = _settingsFactory();
        BindSettings(settings);
        await ApplySettingsAsync(settings, cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            await StopCoreAsync(cancellationToken);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private void BindSettings(PluginSettings settings)
    {
        if (ReferenceEquals(_boundSettings, settings))
        {
            return;
        }

        if (_boundSettings is not null)
        {
            _boundSettings.PropertyChanged -= OnSettingsChanged;
        }

        _boundSettings = settings;
        _boundSettings.PropertyChanged += OnSettingsChanged;
    }

    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Listener-rebuild fields: debounce 500ms
        if (e.PropertyName is nameof(PluginSettings.MediaLinkIsEnabled)
            or nameof(PluginSettings.MediaLinkListenAddress)
            or nameof(PluginSettings.MediaLinkPort)
            or nameof(PluginSettings.MediaLinkToken))
        {
            DebouncedReload();
            return;
        }

        // Hot-reload fields: no rebuild needed
        if (e.PropertyName is nameof(PluginSettings.MediaLinkTimelineMinIntervalMs)
            or nameof(PluginSettings.MediaLinkAllowedOrigins))
        {
            // Timeline interval and allowed origins are read dynamically, no action needed.
            return;
        }
    }

    private void DebouncedReload()
    {
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _debounceCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500, cts.Token);
                if (!cts.Token.IsCancellationRequested)
                {
                    await ReloadAsync();
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    private async Task ReloadAsync()
    {
        try
        {
            await ApplySettingsAsync(_settingsFactory(), CancellationToken.None);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger?.LogWarning(ex, "MediaLink 配置热更新失败");
        }
    }

    private async Task ApplySettingsAsync(PluginSettings settings, CancellationToken cancellationToken)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            await StopCoreAsync(cancellationToken);

            if (!settings.MediaLinkIsEnabled)
            {
                LastError = null;
                return;
            }

            if (string.IsNullOrWhiteSpace(settings.MediaLinkToken))
            {
                settings.MediaLinkToken = MediaLinkAuth.GenerateToken();
            }

            _hub = new MediaLinkSessionHub();
            // 会话增减需通知设置页，否则「N 个客户端」只会在 listener 重建时刷新。
            _hub.SessionCountChanged += OnSessionCountChanged;
            _publisher = new MediaLinkStatePublisher(
                _coordinator,
                _hub,
                () => settings.MediaLinkTimelineMinIntervalMs,
                logger: _loggerFactory?.CreateLogger<MediaLinkStatePublisher>());
            _publisher.Start();

            _server = new MediaLinkServer(
                _hub,
                session => _publisher.PublishSnapshotAsync(session),
                () => settings.MediaLinkToken,
                allowedOriginsAccessor: () => settings.MediaLinkAllowedOrigins,
                injectionStore: _injectionStore,
                coordinator: _coordinator,
                playbackControllerAccessor: () =>
                {
                    try
                    {
                        return _platformProviderResolver.Resolve().PlaybackController;
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug(ex, "MediaLink PlaybackController resolve failed");
                        return null;
                    }
                },
                onEffectiveMediaMutatedAsync: kind =>
                {
                    _coordinator.Recompute(kind);
                    return Task.CompletedTask;
                },
                onEffectiveLyricsMutatedAsync: () =>
                {
                    _coordinator.Recompute(MediaInfoChangeKind.CurrentSession);
                    return Task.CompletedTask;
                },
                logger: _loggerFactory?.CreateLogger<MediaLinkServer>());

            await _server.StartAsync(settings.MediaLinkListenAddress, settings.MediaLinkPort, cancellationToken);
            LastError = null;
            NotifyGatewayChanged();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            NotifyGatewayChanged();
            _logger?.LogError(ex, "MediaLink 服务启动失败");
            await StopCoreAsync(CancellationToken.None);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private void OnSessionCountChanged() =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ActiveSessionCount)));

    private void NotifyGatewayChanged()
    {
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsRunning)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Endpoint)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(LastError)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ActiveSessionCount)));
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        if (_server is not null)
        {
            await _server.StopAsync(cancellationToken);
            await _server.DisposeAsync();
            _server = null;
        }

        _publisher?.Dispose();
        _publisher = null;
        if (_hub is not null)
        {
            _hub.SessionCountChanged -= OnSessionCountChanged;
            _hub = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        if (_boundSettings is not null)
        {
            _boundSettings.PropertyChanged -= OnSettingsChanged;
            _boundSettings = null;
        }

        // 同步等待停止，避免 fire-and-forget 与 _lifecycleLock.Dispose 竞态。
        try
        {
            if (!_lifecycleLock.Wait(DisposeStopTimeout))
            {
                _logger?.LogWarning("MediaLink Dispose 等待生命周期锁超时");
            }
            else
            {
                try
                {
                    StopCoreAsync(CancellationToken.None)
                        .Wait(DisposeStopTimeout);
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "MediaLink Dispose 停止服务时出错");
                }
                finally
                {
                    try { _lifecycleLock.Release(); } catch { /* already disposed/owned */ }
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // ignore
        }

        _lifecycleLock.Dispose();
    }
}
