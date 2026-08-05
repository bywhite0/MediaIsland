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

    /// <summary>宿主未 await StopAsync 时，在 AppStopping 里手动发 1001 的超时上限。</summary>
    private static readonly TimeSpan AppStoppingGoingAwayTimeout = TimeSpan.FromSeconds(3);

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
    private bool _appStoppingHandlerRegistered;

    /// <summary>
    /// 订阅时用的委托实例。必须缓存：Delegate.CreateDelegate 每次返回新实例，
    /// 而事件退订按委托相等性匹配，重新构造的实例移除不掉已订阅的处理器。
    /// </summary>
    private Delegate? _appStoppingDelegate;

    /// <summary>已解析出的 AppBase.Current 实例与 AppStopping 事件，退订时复用。</summary>
    private object? _appStoppingTarget;
    private System.Reflection.EventInfo? _appStoppingEvent;

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

        // ClassIsland 的 App.Stop() 不会 await IHostedService.StopAsync，导致进程退出时
        // 关闭帧来不及发出。订阅 AppStopping 作为纵深防御：即使宿主未正确等待停服，
        // 我们也能在 UI 线程同步拦截中主动发送 1001。
        if (!_appStoppingHandlerRegistered)
        {
            _appStoppingHandlerRegistered = TrySubscribeAppStoppingViaReflection();
        }
    }

    /// <summary>
    /// 通过反射订阅 ClassIsland.Core.AppBase.Current.AppStopping，避免对该程序集的直接引用。
    /// 直接引用会导致测试环境因程序集缺失而 TypeLoadException。
    /// </summary>
    private bool TrySubscribeAppStoppingViaReflection()
    {
        try
        {
            // 尝试加载 ClassIsland.Core 程序集
            var coreAssembly = System.Reflection.Assembly.Load("ClassIsland.Core");
            var appBaseType = coreAssembly.GetType("ClassIsland.Core.AppBase");
            if (appBaseType is null)
            {
                return false;
            }

            var currentProperty = appBaseType.GetProperty("Current", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (currentProperty is null)
            {
                return false;
            }

            var appInstance = currentProperty.GetValue(null);
            if (appInstance is null)
            {
                return false;
            }

            var appStoppingEvent = appBaseType.GetEvent("AppStopping");
            if (appStoppingEvent is null)
            {
                return false;
            }

            // 构造 EventHandler 委托并订阅
            var handlerDelegate = Delegate.CreateDelegate(
                typeof(EventHandler),
                this,
                nameof(OnHostAppStopping));
            appStoppingEvent.AddEventHandler(appInstance, handlerDelegate);

            // 缓存这些对象，退订时复用同一委托实例，否则 RemoveEventHandler 匹配不上
            _appStoppingDelegate = handlerDelegate;
            _appStoppingTarget = appInstance;
            _appStoppingEvent = appStoppingEvent;

            _logger?.LogDebug("MediaLink 已订阅 AppBase.AppStopping");
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "MediaLink 未能订阅 AppStopping，将仅依赖 IHostedService.StopAsync");
            return false;
        }
    }

    /// <summary>通过反射取消 AppStopping 订阅。</summary>
    private void UnsubscribeAppStoppingViaReflection()
    {
        try
        {
            if (_appStoppingEvent is not null && _appStoppingTarget is not null && _appStoppingDelegate is not null)
            {
                _appStoppingEvent.RemoveEventHandler(_appStoppingTarget, _appStoppingDelegate);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "MediaLink 取消 AppStopping 订阅失败");
        }
        finally
        {
            _appStoppingDelegate = null;
            _appStoppingTarget = null;
            _appStoppingEvent = null;
        }
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

    /// <summary>
    /// ClassIsland 的 App.Stop() 不会 await IHostedService.StopAsync，导致进程在 StopAsync 异步
    /// 流水线尚未走完时就 Shutdown()，使得 1001 GoingAway 来不及发出。此处理器订阅同步触发的
    /// AppStopping，在 UI 线程上通过 Task.Run().Wait() 绕开 SynchronizationContext 死锁，
    /// 强制完成 1001 的发送，避免客户端因 1006 而误判网络故障并持续重连。
    /// </summary>
    private void OnHostAppStopping(object? sender, EventArgs e)
    {
        // AppStopping 在 UI 线程同步触发，await 会因 UI 线程阻塞在后续同步代码里而死锁。
        // Task.Run 把工作扔到线程池，Wait() 阻塞当前（UI）线程，但工作本身跑在独立线程上不会死锁。
        var sendTask = Task.Run(async () =>
        {
            if (_hub is null) return;
            try
            {
                await _hub.CloseAllGoingAwayAsync(AppStoppingGoingAwayTimeout, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "MediaLink AppStopping 发送 1001 失败");
            }
        });

        // 阻塞 UI 线程最多 3 秒：宁可稍微延迟退出，也要确保关闭帧发出。
        // 超时后放行，避免卡住主程序。
        try
        {
            sendTask.Wait(AppStoppingGoingAwayTimeout);
        }
        catch (AggregateException)
        {
            // Task.Wait() 包装的异常已在上方 catch 里记录，此处吞掉包装层
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

        if (_appStoppingHandlerRegistered)
        {
            UnsubscribeAppStoppingViaReflection();
            _appStoppingHandlerRegistered = false;
        }

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
