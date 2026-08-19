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
public sealed class AudioPlaybackService : IAudioFrameSubmitter, IDisposable
{
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
            _renderer.Push(frame.Pcm);
            return;
        }

        SubmitToInner(frame);
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
