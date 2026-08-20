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
    private readonly ILogger? _logger;
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
    private long _rejectedFrames;
    private volatile bool _playing;
    private bool _disposed;

    public AudioPlaybackService(IAudioFrameSubmitter inner, IAudioRenderer renderer, ILogger? logger = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _logger = logger;
        _renderer.FramePlayed += OnFramePlayed;
    }

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
    /// 本机输出延迟。出声时等于在用的抖动缓冲深度，否则为零。
    ///
    /// 取目标缓冲深度而不是实测占用（<see cref="AudioRenderStats.RingMs"/>）：占用在目标值
    /// 附近持续波动，把它直接喂给呈现侧会让画面来回抖，而向后跳一下比恒定偏一点更难看。
    /// 目标深度是稳定值，且漂移控制律把占用保持在目标附近，两者的稳态差远小于人可察觉的
    /// 约 50ms。将来若实测出稳态差超过那个量级，再换成平滑后的实测值。
    ///
    /// 不含端点缓冲（共享模式下约 10 到 30ms）：它本身就在可察觉阈值以下，而读到它需要
    /// IAudioClient::GetStreamLatency，那是一次 FFI 与 ABI 变更。它是本量已知的残余误差。
    ///
    /// 判据用实际在播而非请求值：native 缺失时请求为开却没有额外延迟，
    /// 那时减去一个缓冲深度会把画面推到听觉后面，比不补偿更坏。
    /// 而在播必然意味着仲裁认定生效媒体来自上游，故不必再另外判一次音源。
    /// </summary>
    public TimeSpan OutputLatency =>
        _playing ? TimeSpan.FromMilliseconds(Volatile.Read(ref _requestedTargetMs)) : TimeSpan.Zero;

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

            if (!_renderer.IsAvailable)
            {
                LastError = _renderer.FailureReason ?? "音频播放不可用";
                _logger?.LogInformation("[音频:播放] 不可用，回落到不播放：{Reason}", LastError);
                return;
            }

            try
            {
                _renderer.Start(targetBufferMs);
                _playing = true;
                LastError = null;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _logger?.LogWarning(ex, "[音频:播放] 启动失败，回落到不播放。");
            }
        }
    }

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

            _renderer.Push(frame.Pcm);
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

    private void StopUnlocked()
    {
        if (!_playing)
        {
            return;
        }

        _playing = false;
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
