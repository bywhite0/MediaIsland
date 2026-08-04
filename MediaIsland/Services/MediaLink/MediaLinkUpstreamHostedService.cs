using MediaIsland.Models;
using MediaIsland.Services.Media;
using MediaIsland.Services.MediaLink.Mapping;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.ComponentModel;

namespace MediaIsland.Services.MediaLink;

/// <summary>
/// 消费另一台实例的 MediaLink 推送，把收到的媒体与歌词写入注入存储。
///
/// 与 <see cref="MediaLinkHostedService"/>（服务端）相互独立：一台实例可以只推、
/// 只收，或两者同时——后者即为转发中继。
/// </summary>
public sealed class MediaLinkUpstreamHostedService : IHostedService, IDisposable
{
    private readonly MediaLinkInjectionStore _injectionStore;
    private readonly MediaSourceCoordinator _coordinator;
    private readonly Func<PluginSettings> _settingsFactory;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ILogger<MediaLinkUpstreamHostedService>? _logger;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly Func<long> _tickProvider;

    private MediaLinkClient? _client;
    private PluginSettings? _boundSettings;
    private CancellationTokenSource? _debounceCts;
    private bool _disposed;

    public MediaLinkUpstreamHostedService(
        MediaLinkInjectionStore injectionStore,
        MediaSourceCoordinator coordinator,
        Func<PluginSettings> settingsFactory,
        ILoggerFactory? loggerFactory = null,
        Func<long>? tickProvider = null)
    {
        _injectionStore = injectionStore ?? throw new ArgumentNullException(nameof(injectionStore));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _settingsFactory = settingsFactory ?? throw new ArgumentNullException(nameof(settingsFactory));
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<MediaLinkUpstreamHostedService>();
        _tickProvider = tickProvider ?? (() => Environment.TickCount64);
    }

    public bool IsConnected => _client?.IsConnected == true;

    public string? LastError { get; private set; }

    /// <summary>连接状态或配置变化时触发，供设置页刷新，避免轮询。</summary>
    public event EventHandler? StateChanged;

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

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
            await StopClientAsync();
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

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 与服务端同理：在文本框里逐字输入地址/Token 会产生大量无效中间态，
        // 不防抖会反复建连。
        if (e.PropertyName is nameof(PluginSettings.MediaLinkUpstreamIsEnabled)
            or nameof(PluginSettings.MediaLinkUpstreamEndpoint)
            or nameof(PluginSettings.MediaLinkUpstreamToken))
        {
            DebouncedReload();
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
                    await ApplySettingsAsync(_settingsFactory(), CancellationToken.None);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _logger?.LogWarning(ex, "MediaLink 上游配置热更新失败");
            }
        });
    }

    private async Task ApplySettingsAsync(PluginSettings settings, CancellationToken cancellationToken)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            await StopClientAsync();

            if (!settings.MediaLinkUpstreamIsEnabled)
            {
                LastError = null;
                RaiseStateChanged();
                return;
            }

            if (!TryBuildEndpoint(settings.MediaLinkUpstreamEndpoint, out var uri, out var error))
            {
                LastError = error;
                _logger?.LogWarning("MediaLink 上游地址无效：{Error}", error);
                RaiseStateChanged();
                return;
            }

            if (string.IsNullOrWhiteSpace(settings.MediaLinkUpstreamToken))
            {
                LastError = "尚未填写对方的连接密钥";
                RaiseStateChanged();
                return;
            }

            var client = new MediaLinkClient(
                new MediaLinkClientOptions
                {
                    Endpoint = uri,
                    Token = settings.MediaLinkUpstreamToken
                },
                _loggerFactory?.CreateLogger<MediaLinkClient>(),
                _tickProvider);

            client.MediaReceived += OnMediaReceived;
            client.LyricsReceived += OnLyricsReceived;
            client.ConnectionStateChanged += OnClientConnectionStateChanged;
            _client = client;
            client.Start();
            LastError = null;
            RaiseStateChanged();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger?.LogError(ex, "MediaLink 上游客户端启动失败");
            RaiseStateChanged();
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>
    /// 容忍用户只填 <c>host:port</c> 或省略路径：设置页里要求手写完整 ws:// URL
    /// 是没必要的摩擦。
    /// </summary>
    internal static bool TryBuildEndpoint(string? raw, out Uri uri, out string? error)
    {
        uri = null!;
        var text = raw?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            error = "尚未填写对方设备的地址";
            return false;
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "ws://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed))
        {
            error = $"地址格式不正确：{raw}";
            return false;
        }

        if (parsed.Scheme is not ("ws" or "wss"))
        {
            error = "地址格式不正确，请直接填写对方的 IP 和端口，例如 192.168.1.10:17654";
            return false;
        }

        if (parsed.AbsolutePath is "" or "/")
        {
            parsed = new UriBuilder(parsed) { Path = Protocol.MediaLinkProtocol.Path }.Uri;
        }

        uri = parsed;
        error = null;
        return true;
    }

    private void OnMediaReceived(object? sender, MediaLinkMediaReceivedEventArgs e)
    {
        try
        {
            // 回补从收帧到此刻的耗时，否则本机进度会比上游落后这一段。
            var elapsed = _tickProvider() - e.ReceivedAtTick;
            var payload = MediaLinkDtoMapper.ToInjectPayload(e.Media, elapsed);
            if (_injectionStore.TrySetMedia(payload, out var error))
            {
                _coordinator.Recompute(MediaInfoChangeKind.MediaProperties);
                return;
            }

            _logger?.LogDebug("上游媒体注入被拒绝：{Error}", error);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "处理上游媒体失败");
        }
    }

    private void OnLyricsReceived(object? sender, MediaLinkLyricsReceivedEventArgs e)
    {
        try
        {
            if (_injectionStore.TrySetLyrics(e.Lyrics, out var error))
            {
                _coordinator.Recompute(MediaInfoChangeKind.CurrentSession);
                return;
            }

            _logger?.LogDebug("上游歌词注入被拒绝：{Error}", error);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "处理上游歌词失败");
        }
    }

    private void OnClientConnectionStateChanged(object? sender, EventArgs e) => RaiseStateChanged();

    private async Task StopClientAsync()
    {
        if (_client is null)
        {
            return;
        }

        _client.MediaReceived -= OnMediaReceived;
        _client.LyricsReceived -= OnLyricsReceived;
        _client.ConnectionStateChanged -= OnClientConnectionStateChanged;
        await _client.StopAsync();
        _client = null;
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

        try
        {
            StopClientAsync().Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "停止上游客户端时出错");
        }

        _lifecycleLock.Dispose();
    }
}
