using Microsoft.Extensions.Logging;

namespace MediaIsland.Services.Audio.Playback;

/// <summary>
/// 播放开关决定音频帧的去向。装饰器形态包住可视化服务，让上下游都不需要知道
/// 当前处在哪一档。
///
/// 播放关时帧直连内层，可视化贴近写入端、延迟最小——用户的听觉参照在远端，
/// 本地无可对齐对象。播放开时帧只进播放缓冲，由已播出的那一块反过来驱动可视化，
/// 此时可视化延迟等于抖动缓冲深度，但那不是缺陷：可视化的目标从来不是延迟最小，
/// 而是与用户听到的内容对齐。
///
/// 播放起不来时回落直连。不回落的话，打开播放开关会让频谱一起死掉，
/// 而用户的心智模型里这两件事无关，排查方向会指向频谱组件。
/// </summary>
public sealed class AudioPlaybackService : IAudioFrameSubmitter, IAudioOutputLatency, IDisposable
{
    /// <summary>
    /// 渲染器要求的采样率。与 MediaLink 的线格式一致——归一化在 native 采集侧完成，
    /// 到这一层时所有生产者都该已经是这个值。
    /// </summary>
    public const int RequiredSampleRate = 48_000;

    /// <summary>渲染器要求的声道数。</summary>
    public const int RequiredChannels = 2;

    private readonly IAudioFrameSubmitter _inner;
    private readonly IAudioRenderer _renderer;
    private readonly Func<string?> _deviceIdProvider;
    private readonly ILogger? _logger;

    /// <summary>
    /// 毫秒单调钟，默认 <see cref="Environment.TickCount64"/>。可注入沿
    /// <c>AudioClockProbe</c> 的 <c>now100Ns</c> 先例——节流判据要能拨表。
    /// </summary>
    private readonly Func<long> _tickCount64;

    private readonly object _gate = new();

    /// <summary>
    /// 只护住「进内层」这一件事，与 <see cref="_gate"/> 分开是硬要求而非风格。
    ///
    /// 内层的 <c>AudioSampleRing</c> 声明唯一写者，而 <c>Append</c> 是读-改-写；
    /// 停播的瞬间会真的有两个写者——渲染线程的在途回调读到的还是「在播」，
    /// 网络线程已读到「没播」并走直连。
    ///
    /// 而这把锁不能是 <see cref="_gate"/>：<see cref="Configure"/> 持 <see cref="_gate"/>
    /// 调 <c>Stop</c>，后者同步 join 渲染线程，若渲染线程此时在等 <see cref="_gate"/> 就互等。
    /// </summary>
    private readonly object _submitGate = new();

    private bool _requestedEnabled;
    private int _requestedTargetMs;

    /// <summary>
    /// 用户的对齐设置与三个对齐参数，全部在 <c>_gate</c> 下读写。
    ///
    /// 单独存一份而不是每次起播时去问别人：起播时开关要作为 <c>Start</c> 的参数交给
    /// 渲染器（48kHz 端点的内环在那一刻定型）、运行时三项要在起播前下发，
    /// 而起播可能由深度变更触发，那时上游未必在场。
    /// </summary>
    private bool _alignmentEnabled;
    private long _alignmentDTicks;
    private long _alignmentOffsetTicks;
    private long _alignmentManualOffsetTicks;

    /// <summary>
    /// 本次播放会话起播那一刻的对齐开关值。在 <c>_gate</c> 下写，起播成功时记录。
    ///
    /// 与 <see cref="_alignmentEnabled"/> 分开是必须的：后者随用户设置即时变，
    /// 而渲染器里生效的是起播那一刻的值（会话内不可变）。两者不一致即
    /// 「用户改了开关但本会话还没跟上」，中途切换要不要停播重启就比对它。
    /// </summary>
    private bool _sessionAlignmentEnabled;

    /// <summary>输出延迟的采样节流窗口，毫秒。立论见 <see cref="OutputLatency"/>。</summary>
    private const long LatencySampleWindowMs = 100;

    /// <summary>
    /// 采样时刻哨兵：与任何当前时刻的差都远在窗口外，推回它即「首读必采样」。
    /// 取最小值之半，注入的假时钟给负值时相减也不溢出。
    /// </summary>
    private const long LatencyNeverSampledMs = long.MinValue / 2;

    /// <summary>
    /// 输出延迟估计器。target 经起播与停播路径的 <see cref="ResetLatencyUnlocked"/>
    /// 进入，构造初值取多少都读不到——未出声时 <see cref="OutputLatency"/> 的零臂先挡住了。
    /// </summary>
    private readonly OutputLatencyTracker _latencyTracker = new(0);

    /// <summary>
    /// 上次采样输出延迟的时刻，毫秒，取自 <see cref="_tickCount64"/>。Volatile 读写
    /// 不进锁：同窗双读竞态只是偶发多一次 ReadStats（渲染器自有锁），不为它加锁面。
    /// </summary>
    private long _latencySampledAtMs = LatencyNeverSampledMs;

    private long _rejectedFrames;
    private volatile bool _playing;
    private bool _disposed;

    public AudioPlaybackService(
        IAudioFrameSubmitter inner,
        IAudioRenderer renderer,
        ILogger? logger = null,
        Func<string?>? deviceIdProvider = null,
        Func<long>? tickCount64 = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _deviceIdProvider = deviceIdProvider ?? DefaultRenderEndpoint.TryGetId;
        _tickCount64 = tickCount64 ?? (static () => Environment.TickCount64);
        _logger = logger;
        _renderer.FramePlayed += OnFramePlayed;
    }

    /// <summary>
    /// 当前播放设备的标识，per-device 手动偏移用它作键；拿不到时为 null（当未知设备）。
    ///
    /// 语义是「渲染器出声的那个端点」。native 起播时恒绑系统默认渲染端点且不回传 ID，
    /// 故这里按同一条规则取默认端点的 ID，两侧指向同一个端点；播放中换默认设备的
    /// 短暂不一致窗口见 <see cref="DefaultRenderEndpoint"/>。生产接线把 provider 指到
    /// <see cref="DefaultEndpointWatcher"/> 的缓存——每秒全仓只枚举一次，
    /// 本属性读多少遍都不再碰 COM。
    /// </summary>
    public string? CurrentPlaybackDeviceId => _deviceIdProvider();

    /// <summary>真的在出声。请求开启但 native 不可用时为假。</summary>
    public bool IsPlaying => _playing;

    /// <summary>最近一次播放失败的原因。成功时为 null。</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// 上次收到的播放开关请求。
    ///
    /// 记的是请求，不是当前实际状态。请求为开不等于正在出声——那看
    /// <see cref="IsPlaying"/> 与 <see cref="LastError"/>。混淆这两者会把
    /// 「请求为开」误读成「已经在放」，而 native 缺失时前者为真、后者为假。
    ///
    /// 暴露它是为了让接线边可测。播放配置由两跳传来：设置变化让上游服务重算并发出
    /// 音源变化，音源变化再由接线转成一次 <see cref="Configure"/>。这两跳断了都不报错，
    /// 只会让用户改了设置没反应，而在此之前没有任何可观测量能区分接线通与断。
    /// </summary>
    public bool RequestedIsEnabled => Volatile.Read(ref _requestedEnabled);

    /// <summary>
    /// 上次收到的目标缓冲深度请求。语义同 <see cref="RequestedIsEnabled"/>——
    /// 是请求值，不是当前实际在用的深度。
    ///
    /// 用 volatile 读而不进 <see cref="_gate"/>：读这两个值不需要与启停互斥，
    /// 而进锁会多出一个死锁面——<see cref="IAudioRenderer.FramePlayed"/> 的处理器
    /// 已被约定禁止调用渲染器上的任何方法，没有理由让本类的读也落进同一条约束。
    /// </summary>
    public int RequestedTargetBufferMs => Volatile.Read(ref _requestedTargetMs);

    /// <summary>
    /// 因格式失配被丢弃的帧数。
    ///
    /// 暴露它是为了让校验本身可测：丢弃是静默的（不抛、不断流），
    /// 没有这个计数就无法区分「校验拦下了」与「帧根本没来」。
    /// </summary>
    public long RejectedFrameCount => Interlocked.Read(ref _rejectedFrames);

    /// <summary>
    /// 本机输出延迟。出声时为跟踪器折算的实测估计，否则为零。
    ///
    /// 早先这里直接回目标缓冲深度，当时的注释留了「将来若实测出稳态差超过那个量级，
    /// 再换成平滑后的实测值」——接收端卡顿时渲染垫空帧，实际播放位置相对推流位置的
    /// 偏移正是那样的稳态差，如今兑现：交给 <see cref="OutputLatencyTracker"/> 折算每拍
    /// 渲染统计。选路两臂：设备时钟偏移可用时用对齐误差（target 加误差即实际输出延迟），
    /// 不可用时退化读缓冲占用（<see cref="AudioRenderStats.RingMs"/>）；25ms 死区挡住
    /// 占用在目标附近的持续波动，稳态下输出恒定，呈现不抖的既有观感零变化。
    /// 完整立论见跟踪器类注释。
    ///
    /// 采样节流 100ms：<see cref="IAudioRenderer.ReadStats"/> 与渲染送帧的 Push 同一把锁，
    /// 歌词 80ms 节拍与词模式的高频 tick 不得逐次进锁。100ms 窗口内缓慢漂移能积累的量
    /// 在死区以下，节流不引入死区之外的误差；垫零跳变最多晚一拍被跟上，歌词行粒度下
    /// 不可见。起播把采样时刻推回窗口外，首读必采样；同窗双读竞态只是偶发多一次
    /// ReadStats（渲染器自有锁），不为它加锁面，本属性维持无锁读。
    ///
    /// 更宽的竞态同样显式接受：读者过了在播检查后挂起、横跨整个停播重启的
    /// in-flight 采样，会把旧会话的 stats 喂进新会话的 tracker——自愈不超过一拍，
    /// 哨兵已推回、下拍必重采样，残差再由死区吞掉，不设防。
    ///
    /// 不含端点缓冲（共享模式下约 10 到 30ms）：它本身就在可察觉阈值以下，而读到它需要
    /// IAudioClient::GetStreamLatency，那是一次 FFI 与 ABI 变更。它是本量已知的残余误差。
    ///
    /// 判据用实际在播而非请求值：native 缺失时请求为开却没有额外延迟，
    /// 那时减去一个缓冲深度会把画面推到听觉后面，比不补偿更坏。
    /// 而在播必然意味着仲裁认定生效媒体来自上游，故不必再另外判一次音源。
    /// </summary>
    public TimeSpan OutputLatency
    {
        get
        {
            if (!_playing)
            {
                return TimeSpan.Zero;
            }

            var now = _tickCount64();
            if (now - Volatile.Read(ref _latencySampledAtMs) >= LatencySampleWindowMs)
            {
                Volatile.Write(ref _latencySampledAtMs, now);
                var stats = _renderer.ReadStats();
                _latencyTracker.Sample(
                    stats.HasStarted, stats.ClockOffsetAvailable, stats.RingMs, stats.PlayTimeErrorUs);
            }

            return _latencyTracker.Current;
        }
    }

    /// <summary>
    /// 设置播放开关与目标缓冲深度。幂等：由设置变化与仲裁变化共同触发，会被反复调用，
    /// 每次都重启 renderer 会让播放一顿一顿。
    ///
    /// 幂等判据比的是请求值，加上实际是否在播。少了后者，一次起播失败会把播放
    /// 永久卡在关闭态——请求值已经记成「开」，此后同值调用全被当成无变化早退，
    /// 而设置页显示的是开。
    ///
    /// 深度变更走停播重启，不做在线调整：深度变更要么丢音要么静音填充，
    /// 两者都不如一次干净的重启，且这是罕见操作。
    /// </summary>
    public void Configure(bool enabled, int targetBufferMs)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var unchanged = enabled == _requestedEnabled
                && targetBufferMs == _requestedTargetMs
                && enabled == _playing;
            if (unchanged)
            {
                return;
            }

            _requestedEnabled = enabled;
            _requestedTargetMs = targetBufferMs;

            StopUnlocked();

            if (!enabled)
            {
                LastError = null;
                return;
            }

            StartUnlocked();
        }
    }

    /// <summary>
    /// 默认渲染端点变化后的重启通道。设备事实全体作废（偏移的键、延迟、缓冲都是
    /// per-device 的），比深度变更更甚，走同一条「停播重启」先例；重启按暂存请求值
    /// 起播，偏移与事实由重启后的协调方重算按新设备重取。
    ///
    /// 条件比的是请求值而非 <see cref="IsPlaying"/>：上一次重启失败后播放停在关闭态
    /// 而请求还开着，设备再变必须还能重试（错误处理约定如此）——比在播的话一次失败
    /// 就永久聋。未请求播放时零动作，watcher 的事件与本方法都不越权。
    /// </summary>
    public void RestartForDeviceChange()
    {
        lock (_gate)
        {
            if (_disposed || !_requestedEnabled)
            {
                return;
            }

            StopUnlocked();
            StartUnlocked();
        }
    }

    /// <summary>
    /// 起播块，Configure、设备变化重启与开关中途切换共用。按暂存请求值
    /// （<see cref="_requestedTargetMs"/>）与当前对齐设置起播，失败走
    /// <see cref="LastError"/> 回落——三条入口的错误形态必须一致，否则重启失败
    /// 的归因就与起播失败长得不一样。
    /// </summary>
    private void StartUnlocked()
    {
        if (!_renderer.IsAvailable)
        {
            LastError = _renderer.FailureReason ?? "音频播放不可用";
            _logger?.LogInformation("[音频:播放] 不可用，回落到不播放：{Reason}", LastError);
            return;
        }

        try
        {
            // 运行时三项在 Start 之前下发，外环起步的第一轮就读到起播前已知的
            // offset；开关本身是 Start 的参数——48kHz 端点的内环在起播那一刻
            // 按它定型，时序错误在类型上写不出来。
            ApplyAlignmentUnlocked();
            _renderer.Start(_requestedTargetMs, _alignmentEnabled);
            _sessionAlignmentEnabled = _alignmentEnabled;
            ResetLatencyUnlocked();
            _playing = true;
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger?.LogWarning(ex, "[音频:播放] 启动失败，回落到不播放。");
        }
    }

    /// <summary>
    /// 下发跨机对齐参数。
    ///
    /// <paramref name="enabled"/> 只表示用户开没开对齐，不兼作 offset 的可用性——
    /// offset 不可用由 <paramref name="offsetTicks"/> 为 0 表示。两者分开是因为它们的
    /// 生命周期不同：开关在起播那一刻决定要不要建内环的执行器（作为
    /// <see cref="IAudioRenderer.Start"/> 的参数传递，会话内不可变），而 offset 随
    /// 对时结果每秒都可能变。把「offset 还没算出来」写成「对齐关着」，起播时就不会
    /// 建执行器，几秒后 offset 到了也无处施力。
    ///
    /// 播放中调用时运行时三项即时生效；开关与会话起播值不一致且在播 → 就地停播重启。
    /// enabled 并进 render_start 之后，重启是中途切换唯一可能的生效方式——代价是
    /// 一次可闻的中断，但用户刚拨了一个播放语义的开关，中断在预期之内。
    /// 同值绝不重启：对时结果每秒下发走的就是本方法，幂等判据钉死这一条，
    /// 防止把探测节奏变成每秒重启。
    /// </summary>
    public void ConfigureAlignment(
        bool enabled, long dTicks, long offsetTicks, long manualOffsetTicks)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _alignmentEnabled = enabled;
            _alignmentDTicks = dTicks;
            _alignmentOffsetTicks = offsetTicks;
            _alignmentManualOffsetTicks = manualOffsetTicks;

            // 未起播时只存着：起播路径会在 Start 之前下发三项、经 Start 传开关。
            if (!_playing)
            {
                return;
            }

            if (enabled != _sessionAlignmentEnabled)
            {
                // 开关中途切换。StartUnlocked 会在新 Start 前补发刚存下的三项，
                // 并按暂存请求深度起播——换的只是开关，不动用户的其余配置。
                StopUnlocked();
                StartUnlocked();
                return;
            }

            ApplyAlignmentUnlocked();
        }
    }

    /// <summary>
    /// 本次播放会话起播那一刻的对齐开关值。不在播时读到的是上一会话的残值，
    /// 先看 <see cref="IsPlaying"/>。与 <c>ConfigureAlignment</c> 存下的请求值不一致
    /// 即「用户改了开关但本会话还没跟上」——中途切换的重启判断比对的就是它。
    /// </summary>
    public bool SessionAlignmentEnabled
    {
        get
        {
            lock (_gate)
            {
                return _sessionAlignmentEnabled;
            }
        }
    }

    private void ApplyAlignmentUnlocked() => _renderer.SetAlignment(
        _alignmentDTicks, _alignmentOffsetTicks, _alignmentManualOffsetTicks);

    /// <summary>
    /// 读对齐判定要的本机事实。缓冲容量按设备采样率换算——DeviceBufferFrames 是设备帧。
    /// 未起播时两项设备量为零，HasStarted 一并带出，调用方据此区分「延迟为零」与「还不知道」。
    /// </summary>
    public AudioAlignmentFacts ReadAlignmentFacts()
    {
        var stats = _renderer.ReadStats();
        var bufferMs = stats.DeviceSampleRate > 0
            ? stats.DeviceBufferFrames * 1_000.0 / stats.DeviceSampleRate
            : 0;
        return new AudioAlignmentFacts(
            stats.HasStarted,
            stats.DeviceLatencyUs / 1_000.0,
            bufferMs,
            _renderer.TargetDepthBounds().MinTargetMs,
            stats.TargetMsCurrent,
            stats.PlayTimeErrorUs);
    }

    /// <summary>
    /// 目标深度可取的区间，透传渲染实现的常量。饱和判定与设置页状态行要的是两端；
    /// 事实组里只带下界——上界在可行性判定里没有位置，不值得为它扩事实。
    /// 常量不随播放状态变，读它不进锁。
    /// </summary>
    public (int MinTargetMs, int MaxTargetMs) TargetDepthBounds() => _renderer.TargetDepthBounds();

    public void Submit(AudioFrame frame)
    {
        // 分支判定不进锁：本方法在网络收循环上，50 帧每秒。_playing 是 volatile，
        // 切换瞬间最多让一帧走错分支，那比每帧抢启停的锁划算。
        if (_playing)
        {
            // 渲染器的契约是 48000Hz 与 2 声道 i16 交错，而在此之前没有任何一处强制它。
            // 当前两个生产者都已归一，故这条分支不可达；第三个帧源进来时会静默播错速，
            // 那比崩溃难查——声音出来了，只是不对。
            //
            // 丢弃而不抛：本方法在网络收循环上，抛异常会把一帧的格式问题升级成断流。
            // 与转发侧对失配帧不出网的处置一致。
            if (frame.SampleRate != RequiredSampleRate || frame.Channels != RequiredChannels)
            {
                RejectFrame(frame);
                return;
            }

            // 发送端时刻原样透传。要不要走时间轴由 native 侧判：时刻非零且对齐开着才走，
            // 故对齐关闭或帧不带时刻（本机采集、老对端）时播放仍逐字走原路径。
            _renderer.Push(frame.Pcm, frame.SenderTimelineTicks100Ns);
            return;
        }

        SubmitToInner(frame);
    }

    /// <summary>
    /// 记一次格式失配。只在第一帧落日志——失配是持续性的（源不会一帧一个格式），
    /// 50 帧每秒逐帧记会把日志刷爆，而第一条已经说清了是什么格式对不上。
    /// </summary>
    private void RejectFrame(AudioFrame frame)
    {
        var count = Interlocked.Increment(ref _rejectedFrames);
        if (count == 1)
        {
            _logger?.LogWarning(
                "[音频:播放] 丢弃格式失配的帧：{Rate}Hz/{Channels} 声道，播放器只接 {RequiredRate}Hz/{RequiredChannels} 声道。",
                frame.SampleRate,
                frame.Channels,
                RequiredSampleRate,
                RequiredChannels);
        }
    }

    /// <summary>
    /// 已播出的一块。停播后仍可能收到在途回调，此时帧路径已切回直连，
    /// 让它继续进内层会与直连帧交错，频谱会出现时间倒流。
    /// </summary>
    private void OnFramePlayed(AudioFrame frame)
    {
        if (_playing)
        {
            SubmitToInner(frame);
        }
    }

    /// <summary>
    /// 内层的唯一入口。两条帧路径（直连与已播出）在切换瞬间会并发到达，
    /// 而内层只受得住一个写者，故这里串行化。见 <see cref="_submitGate"/> 的说明。
    /// </summary>
    private void SubmitToInner(AudioFrame frame)
    {
        lock (_submitGate)
        {
            _inner.Submit(frame);
        }
    }

    /// <summary>
    /// 让输出延迟估计回到「target 即估计、首读必采样」态。起播成功时调用，
    /// 新会话按当次请求深度起步；停播时同样调用，幂等清残态防跨会话粘值——
    /// 停播期的读虽被零臂挡住，残值也不该活过会话。恒在 <c>_gate</c> 下调用。
    /// </summary>
    private void ResetLatencyUnlocked()
    {
        _latencyTracker.Reset(_requestedTargetMs);
        Volatile.Write(ref _latencySampledAtMs, LatencyNeverSampledMs);
    }

    private void StopUnlocked()
    {
        if (!_playing)
        {
            return;
        }

        _playing = false;
        ResetLatencyUnlocked();
        try
        {
            _renderer.Stop();
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[音频:播放] 停止时出错");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _renderer.FramePlayed -= OnFramePlayed;
            StopUnlocked();
        }
    }
}
