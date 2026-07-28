using MediaIsland.Models;
using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Media;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MediaIsland.Services.MediaLink;

public sealed class MediaLinkHostedService : IHostedService, IMediaLinkGateway, IDisposable
{
    private readonly IMediaService _mediaService;
    private readonly LyricsSearchService _lyricsSearchService;
    private readonly Func<PluginSettings> _settingsFactory;
    private readonly Func<string> _configFolderFactory;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ILogger<MediaLinkHostedService>? _logger;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    private MediaLinkSessionHub? _hub;
    private MediaLinkStatePublisher? _publisher;
    private MediaLinkServer? _server;
    private PluginSettings? _boundSettings;
    private bool _disposed;

    public MediaLinkHostedService(
        IMediaService mediaService,
        LyricsSearchService lyricsSearchService,
        Func<PluginSettings> settingsFactory,
        Func<string> configFolderFactory,
        ILoggerFactory? loggerFactory = null)
    {
        _mediaService = mediaService;
        _lyricsSearchService = lyricsSearchService;
        _settingsFactory = settingsFactory;
        _configFolderFactory = configFolderFactory;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<MediaLinkHostedService>();
    }

    public bool IsRunning => _server?.IsRunning == true;

    public string? Endpoint => _server?.Endpoint;

    // Certificate store removed; fingerprint UI residual until Task 5/settings rewrite.
    public string? CertFingerprint => null;

    public string? LastError { get; private set; }

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
        if (e.PropertyName is nameof(PluginSettings.RealtimeIsEnabled)
            or nameof(PluginSettings.RealtimeListenAddress)
            or nameof(PluginSettings.RealtimePort)
            or nameof(PluginSettings.RealtimeToken)
            or nameof(PluginSettings.RealtimeTimelineMinIntervalMs))
        {
            _ = ReloadAsync();
        }
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

            if (!settings.RealtimeIsEnabled)
            {
                LastError = null;
                return;
            }

            if (string.IsNullOrWhiteSpace(settings.RealtimeToken))
            {
                settings.RealtimeToken = MediaLinkAuth.GenerateToken();
            }

            // configFolder retained for future MediaLink data dir; cert store path removed.
            _ = _configFolderFactory();

            _hub = new MediaLinkSessionHub();
            _publisher = new MediaLinkStatePublisher(
                _mediaService,
                _lyricsSearchService,
                _hub,
                () => settings.RealtimeTimelineMinIntervalMs,
                logger: _loggerFactory?.CreateLogger<MediaLinkStatePublisher>());
            _publisher.Start();

            _server = new MediaLinkServer(
                _hub,
                session => _publisher.PublishSnapshotAsync(session),
                () => settings.RealtimeToken,
                _loggerFactory?.CreateLogger<MediaLinkServer>());

            await _server.StartAsync(settings.RealtimeListenAddress, settings.RealtimePort, cancellationToken);
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger?.LogError(ex, "MediaLink 服务启动失败");
            await StopCoreAsync(CancellationToken.None);
        }
        finally
        {
            _lifecycleLock.Release();
        }
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
        _hub = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_boundSettings is not null)
        {
            _boundSettings.PropertyChanged -= OnSettingsChanged;
        }

        _ = StopAsync(CancellationToken.None);
        _lifecycleLock.Dispose();
        _disposed = true;
    }
}
