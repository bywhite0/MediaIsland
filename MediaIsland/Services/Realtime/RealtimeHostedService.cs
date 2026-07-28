using MediaIsland.Models;
using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Media;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MediaIsland.Services.Realtime;

public sealed class RealtimeHostedService : IHostedService, IRealtimeGateway, IDisposable
{
    private readonly IMediaService _mediaService;
    private readonly LyricsSearchService _lyricsSearchService;
    private readonly Func<PluginSettings> _settingsFactory;
    private readonly Func<string> _configFolderFactory;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ILogger<RealtimeHostedService>? _logger;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    private RealtimeCertificateStore? _certificateStore;
    private RealtimeSessionHub? _hub;
    private RealtimeStatePublisher? _publisher;
    private RealtimeServer? _server;
    private PluginSettings? _boundSettings;
    private bool _disposed;

    public RealtimeHostedService(
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
        _logger = loggerFactory?.CreateLogger<RealtimeHostedService>();
    }

    public bool IsRunning => _server?.IsRunning == true;

    public string? Endpoint => _server?.Endpoint;

    public string? CertFingerprint => _certificateStore is null
        ? null
        : SafeFingerprint();

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
            _logger?.LogWarning(ex, "Realtime 配置热更新失败");
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
                settings.RealtimeToken = RealtimeAuth.GenerateToken();
            }

            var realtimeDir = Path.Combine(_configFolderFactory(), "realtime");
            _certificateStore = new RealtimeCertificateStore(realtimeDir);
            _hub = new RealtimeSessionHub();
            _publisher = new RealtimeStatePublisher(
                _mediaService,
                _lyricsSearchService,
                _hub,
                () => settings.RealtimeTimelineMinIntervalMs,
                logger: _loggerFactory?.CreateLogger<RealtimeStatePublisher>());
            _publisher.Start();

            _server = new RealtimeServer(
                _certificateStore,
                _hub,
                session => _publisher.PublishSnapshotAsync(session),
                () => settings.RealtimeToken,
                _loggerFactory?.CreateLogger<RealtimeServer>());

            await _server.StartAsync(settings.RealtimeListenAddress, settings.RealtimePort, cancellationToken);
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger?.LogError(ex, "Realtime 服务启动失败");
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
        _certificateStore = null;
    }

    private string? SafeFingerprint()
    {
        try
        {
            _certificateStore?.EnsureCertificate();
            return _certificateStore?.CertFingerprint;
        }
        catch
        {
            return null;
        }
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
