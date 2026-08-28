using Microsoft.Extensions.Logging;

namespace MediaIsland.Services.Audio.Playback;

/// <summary>
/// 默认渲染端点的变化检测器，兼作端点 ID 的最近读值缓存。
///
/// 共享模式的 WASAPI 流不随默认设备切换迁移（见 <see cref="DefaultRenderEndpoint"/>）。
/// 默认设备变了之后声音仍从旧端点出，而按规则取到的是新端点 ID——不检测的话这个
/// 不一致窗口无限长，直到下一次自然重启。检测用 1 Hz 轮询而不用 IMMNotificationClient：
/// 要的是变化后秒级跟上，不是毫秒级；轮询复用既有 COM 声明、零新 COM 表面、
/// 漏一拍下一拍自愈，而托管侧实现 COM 回调对象是三个新失效面（对象生命周期、
/// MTA 回调线程、每角色多次回调去抖），订阅丢了就永久聋。
///
/// 变化判定只认「非 null → 不同的非 null」，比对基线是最近一个非 null 读值：
/// 变成 null 是设备消失，归拔线路径（WASAPI 错误返回自己会到），这里只记录不动作；
/// 从 null 恢复到与消失前不同的端点按变化处理，恢复到同一个端点不算——流若还活着
/// 不必重启，流已死走的是错误路径的既有恢复，不归本类越权代办。
///
/// 轮询只在有播放/采集会话或设置页可见时枚举。节拍照走，门关着时每拍只花一次
/// 委托求值：会话门与页面可见性之外没有读点需要这个值，闲时不必每秒抓一次 COM。
/// 门刚从关转开的首个枚举拍静默对齐基线而不比对——关门时段的历史差异不属于「变化」。
/// </summary>
public sealed class DefaultEndpointWatcher : IDisposable
{
    private readonly Func<string?> _idProvider;
    private readonly Func<bool> _sessionGate;
    private readonly ILogger? _logger;
    private readonly Timer? _timer;
    private readonly object _gate = new();
    private string? _cachedId;
    private string? _lastNonNullId;
    private bool _lastBeatEnumerated;
    private int _uiVisibleCount;
    private int _polling;
    private volatile bool _disposed;

    /// <summary>
    /// 默认渲染端点从一个设备变成了另一个设备。事件只在节拍拍上派发，处理器在轮询线程
    /// （生产为计时器线程）上执行；进场拍只刷缓存，不派发本事件。新值不随事件携带——
    /// 消费方要的是「按当下状态重启」，读 <see cref="CachedId"/> 即可，事件发出前缓存已更新。
    /// </summary>
    public event EventHandler? DefaultEndpointChanged;

    /// <summary>
    /// <paramref name="sessionGate"/> 表示「有播放或采集会话」，与设置页可见性取或后
    /// 决定这一拍枚不枚举；省略即恒真（判据用手动泵驱动、不关心门控的场景）。
    /// <paramref name="pollInterval"/> 给定即自带节拍（生产 1 Hz），省略则完全由
    /// <see cref="Poll"/> 的调用方供拍。
    /// </summary>
    public DefaultEndpointWatcher(
        Func<string?> deviceIdProvider,
        Func<bool>? sessionGate = null,
        TimeSpan? pollInterval = null,
        ILogger? logger = null)
    {
        _idProvider = deviceIdProvider ?? throw new ArgumentNullException(nameof(deviceIdProvider));
        _sessionGate = sessionGate ?? (static () => true);
        _logger = logger;
        if (pollInterval is { } interval)
        {
            _timer = new Timer(
                static state => ((DefaultEndpointWatcher)state!).Poll(), this, interval, interval);
        }
    }

    /// <summary>
    /// 最近一拍读到的端点 ID；null 表示此刻无设备或还没轮询过。
    /// <c>CurrentPlaybackDeviceId</c> 改读它，每秒全仓只枚举一次，读多少遍都不再碰 COM。
    /// 会话刚建立、下一拍还没到时它可能落后真实设备至多一个轮询周期——这个窗口里
    /// 按旧值取偏移不劣于此前恒传 0 的行为，下一拍自愈。
    /// </summary>
    public string? CachedId
    {
        get
        {
            lock (_gate)
            {
                return _cachedId;
            }
        }
    }

    /// <summary>
    /// 设置页的可见性开门。计数而非布尔：页面重建时新页的进场可能先于旧页的离场，
    /// 布尔会被后到的离场误关。进场拍只刷 <see cref="CachedId"/>，页面打开第一眼就有值；
    /// 不比对、不发事件、不动基线——重启链因此永不在 UI 线程执行，而恰在进场前发生的
    /// 变化仍由下一个节拍拍在计时器线程上按旧基线检出，不吞变化。
    /// </summary>
    public void SetUiVisible(bool visible)
    {
        if (visible)
        {
            Interlocked.Increment(ref _uiVisibleCount);
            RefreshCache();
            return;
        }

        if (Interlocked.Decrement(ref _uiVisibleCount) < 0)
        {
            Interlocked.Increment(ref _uiVisibleCount);
        }
    }

    /// <summary>
    /// 进场拍：读一次设备 ID 只刷 <see cref="CachedId"/>，不比对、不发事件、不动基线、
    /// 不动恢复拍追踪。与 <see cref="Poll"/> 共用同一进入闸，任何时刻至多一次枚举在跑。
    /// </summary>
    private void RefreshCache()
    {
        if (_disposed || Interlocked.Exchange(ref _polling, 1) != 0)
        {
            return;
        }

        try
        {
            string? id;
            try
            {
                id = _idProvider();
            }
            catch
            {
                // 与 Poll 的兜底同义：任意异常折成「此刻无设备」。
                id = null;
            }

            lock (_gate)
            {
                _cachedId = id;
            }
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    /// <summary>
    /// 泵一拍：门开着就枚举一次，与最近的非 null 读值比对，不同的非 null 才发变化事件。
    /// 门刚从关转开的首个枚举拍（含从未枚举过的首拍）是恢复拍，只静默对齐不比对。
    /// 生产由构造时的计时器供拍，判据手动调用。整体不抛：计时器线程上没有人接得住
    /// 往外抛的东西。
    /// </summary>
    public void Poll()
    {
        if (_disposed || Interlocked.Exchange(ref _polling, 1) != 0)
        {
            return;
        }

        try
        {
            if (!ShouldEnumerate())
            {
                _lastBeatEnumerated = false;
                return;
            }

            string? id;
            try
            {
                id = _idProvider();
            }
            catch
            {
                // 生产的 provider 自己把 COM 失败折成 null；这里再兜一层，语义相同：
                // 此刻无设备，只记录不动作，下一拍自愈。
                id = null;
            }

            var recovering = !_lastBeatEnumerated;
            _lastBeatEnumerated = true;

            bool changed;
            lock (_gate)
            {
                _cachedId = id;
                // 恢复拍（上一拍没枚举，含首拍）静默对齐、不比对：门关时段无会话在跑，
                // 开门首拍看到的差异全是历史，对齐基线即可；门开着时枚举连续，
                // 真变化走原路，故本分支吞不到真变化。
                changed = !recovering
                    && id is not null
                    && _lastNonNullId is not null
                    && !string.Equals(_lastNonNullId, id, StringComparison.Ordinal);
                if (id is not null)
                {
                    _lastNonNullId = id;
                }
            }

            if (changed)
            {
                _logger?.LogInformation("[音频] 默认渲染端点已变化，重启在播会话以跟随新设备。");
                DefaultEndpointChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            // 处理器炸了不能带崩计时器线程。重启通道各自有错误出口，这里只记账。
            _logger?.LogWarning(ex, "[音频] 设备变化的跟随处理失败，设备再变时会重试。");
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    private bool ShouldEnumerate()
    {
        if (Volatile.Read(ref _uiVisibleCount) > 0)
        {
            return true;
        }

        try
        {
            return _sessionGate();
        }
        catch
        {
            // 门自身出错（容器已释放之类）按关处理：宁可少枚举一拍，不在计时器线程上炸。
            return false;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer?.Dispose();
    }
}
