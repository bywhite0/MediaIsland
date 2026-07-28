using MediaIsland.Models;
using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Media;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MediaIsland.Services.MediaLink;

public sealed class MediaLinkHostedService : IHostedService, IMediaLinkGateway, IDisposable
{
    private static readonly TimeSpan DisposeStopTimeout = TimeSpan.FromSeconds(5);

    private readonly IMediaService _mediaService;
    private readonly LyricsSearchService _lyricsSearchService;
    private readonly Func<PluginSettings> _settingsFactory;
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
        ILoggerFactory? loggerFactory = null)
    {
        _mediaService = mediaService;
        _lyricsSearchService = lyricsSearchService;
        _settingsFactory = settingsFactory;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<MediaLinkHostedService>();
    }

    public bool IsRunning => _server?.IsRunning == true;

    public string? Endpoint => _server?.Endpoint;

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
        if (e.PropertyName is nameof(PluginSettings.MediaLinkIsEnabled)
            or nameof(PluginSettings.MediaLinkListenAddress)
            or nameof(PluginSettings.MediaLinkPort)
            or nameof(PluginSettings.MediaLinkToken)
            or nameof(PluginSettings.MediaLinkTimelineMinIntervalMs))
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
            _publisher = new MediaLinkStatePublisher(
                _mediaService,
                _lyricsSearchService,
                _hub,
                () => settings.MediaLinkTimelineMinIntervalMs,
                logger: _loggerFactory?.CreateLogger<MediaLinkStatePublisher>());
            _publisher.Start();

            _server = new MediaLinkServer(
                _hub,
                session => _publisher.PublishSnapshotAsync(session),
                () => settings.MediaLinkToken,
                _loggerFactory?.CreateLogger<MediaLinkServer>());

            await _server.StartAsync(settings.MediaLinkListenAddress, settings.MediaLinkPort, cancellationToken);
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

        _disposed = true;

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
