using MediaIsland.Models;
using MediaIsland.Services.Audio.Visualization;
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
    private readonly AudioVisualizationService? _visualization;
    private readonly AudioVisualizationDemand? _demand;

    private MediaLinkClient? _client;
    private MediaLinkAudioReceiver? _audioReceiver;
    private PluginSettings? _boundSettings;
    private CancellationTokenSource? _debounceCts;
    private bool _upstreamAudioEnabled;
    private bool _disposed;

    public MediaLinkUpstreamHostedService(
        MediaLinkInjectionStore injectionStore,
        MediaSourceCoordinator coordinator,
        Func<PluginSettings> settingsFactory,
        ILoggerFactory? loggerFactory = null,
        Func<long>? tickProvider = null,
        AudioVisualizationService? visualization = null,
        AudioVisualizationDemand? visualizationDemand = null)
    {
        _injectionStore = injectionStore ?? throw new ArgumentNullException(nameof(injectionStore));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _settingsFactory = settingsFactory ?? throw new ArgumentNullException(nameof(settingsFactory));
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<MediaLinkUpstreamHostedService>();
        _tickProvider = tickProvider ?? (() => Environment.TickCount64);
        _visualization = visualization;
        _demand = visualizationDemand;
    }

    public bool IsConnected => _client?.IsConnected == true;

    /// <summary>
    /// 是否正在消费上游音频。用连接态判断，不用帧流量判断——
    /// 按「最近 N 毫秒有没有收到帧」来定，上游断续时采集会反复启停，
    /// 得加迟滞、加时间窗，是一串补丁。
    ///
    /// 上游连着但对方暂停时收到的是静音帧（第 2 期保证暂停不断流），
    /// 频谱归零正是正确表现，不该回落本机——回落会让用户看到本机的声音
    /// 却以为那是远端的。
    /// </summary>
    public bool IsConsumingUpstreamAudio => ShouldConsumeUpstreamAudio(
        _upstreamAudioEnabled, IsConnected, _client?.SupportsAudio == true, _demand?.IsDemanded == true);

    /// <summary>
    /// 仲裁本身。抽成静态纯函数，让这四个条件的组合可以被真值表逐格锁死——
    /// 端到端驱动它需要真实 listener、真实 socket 与真实音频设备，而它的全部内容
    /// 就是四个布尔量。
    /// </summary>
    internal static bool ShouldConsumeUpstreamAudio(
        bool upstreamEnabled, bool connected, bool supportsAudio, bool visualizationDemanded) =>
        upstreamEnabled && connected && supportsAudio && visualizationDemanded;

    public string? LastError { get; private set; }

    /// <summary>连接状态或配置变化时触发，供设置页刷新，避免轮询。</summary>
    public event EventHandler? StateChanged;

    /// <summary>音源仲裁结果可能已变。服务端据此重算本机采集需求。</summary>
    public event EventHandler? AudioSourceChanged;

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    private void RaiseAudioSourceChanged() => AudioSourceChanged?.Invoke(this, EventArgs.Empty);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_demand is not null)
        {
            _demand.DemandChanged += OnVisualizationDemandChanged;
        }

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

            if (_visualization is not null)
            {
                // trackToken 与歌词同源：切歌后过期曲目的 PCM 由接收侧丢弃。
                _audioReceiver = new MediaLinkAudioReceiver(
                    _visualization,
                    () => _injectionStore.GetMediaSnapshot() is { } media
                        ? MediaLinkDtoMapper.ComputeTrackToken(
                            media.SourceApp, media.Title, media.Artist, media.AlbumTitle)
                        : null,
                    _loggerFactory?.CreateLogger<MediaLinkAudioReceiver>());
                client.AudioFrameReceived += OnAudioFrameReceived;
            }

            _client = client;
            _upstreamAudioEnabled = true;
            client.Start();

            // 连上之后按当前需求补一次订阅意愿：组件可能在断线期间就挂载好了。
            if (_demand?.IsDemanded == true)
            {
                await client.SetAudioSubscribedAsync(true);
            }

            LastError = null;
            RaiseStateChanged();
            RaiseAudioSourceChanged();
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

    private void OnClientConnectionStateChanged(object? sender, EventArgs e)
    {
        RaiseStateChanged();
        // 连接态是仲裁的入参之一：断连必须让服务端重算，否则本机采集不会自动接上。
        RaiseAudioSourceChanged();
    }

    private void OnAudioFrameReceived(object? sender, MediaLinkAudioFrameReceivedEventArgs e)
    {
        try
        {
            _audioReceiver?.Handle(e.Frame);
        }
        catch (Exception ex)
        {
            // 单帧处理失败不该拖垮收循环——那会把一次数据问题升级成一次断连。
            _logger?.LogDebug(ex, "处理上游音频帧失败");
        }
    }

    /// <summary>
    /// 可视化需求变化：重算而非增减。订阅与本机采集同构——都是「有消费者才占资源」，
    /// 无消费者时订阅 audio 会让上游白采集、白传约 192KB/s。
    /// </summary>
    private void OnVisualizationDemandChanged()
    {
        var client = _client;
        if (client is null)
        {
            RaiseAudioSourceChanged();
            return;
        }

        // 不在事件线程上 await：需求变化由组件的 Loaded/Unloaded 触发，那是 UI 线程。
        _ = Task.Run(async () =>
        {
            try
            {
                await client.SetAudioSubscribedAsync(_demand?.IsDemanded == true);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "切换上游音频订阅失败");
            }
            finally
            {
                RaiseAudioSourceChanged();
            }
        });
    }

    private async Task StopClientAsync()
    {
        if (_client is null)
        {
            _upstreamAudioEnabled = false;
            return;
        }

        _client.MediaReceived -= OnMediaReceived;
        _client.LyricsReceived -= OnLyricsReceived;
        _client.ConnectionStateChanged -= OnClientConnectionStateChanged;
        _client.AudioFrameReceived -= OnAudioFrameReceived;
        await _client.StopAsync();
        _client = null;
        _audioReceiver = null;
        _upstreamAudioEnabled = false;
        RaiseAudioSourceChanged();
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

        if (_demand is not null)
        {
            _demand.DemandChanged -= OnVisualizationDemandChanged;
        }

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
