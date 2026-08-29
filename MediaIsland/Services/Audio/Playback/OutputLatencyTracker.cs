namespace MediaIsland.Services.Audio.Playback;

/// <summary>
/// 输出延迟跟踪器。接收端卡顿时渲染垫空帧,实际播放位置相对推流位置产生偏移,
/// 起播常量不再等于真实输出延迟,歌词便不跟偏移。本类是纯状态跟踪器:
/// 把每拍渲染统计折算成当前输出延迟估计,不做任何 IO,接线在播放服务侧。
///
/// 死区取 25ms 的来历:推流块与渲染消费块各约 10ms 的量化噪声叠加后峰峰约 20ms,
/// 死区须在其上;50ms 是可察觉阈值,取其半以下。稳态下输出恒定,
/// 呈现不抖的既有观感零变化。
///
/// 一步跟随而非限速滑动:垫零偏移是一次性事件,跟一步永久正确;限速引入时间态,
/// 复杂度不换收益。缓慢单调漂移被死区挡成 25ms 步长的阶梯跟随,
/// 歌词行粒度(秒级)下不可见。
///
/// prefill 或硬重置重攒期(垫零无声,可达一秒)占用从 0 爬回 target,
/// 输出会先跟到低值再爬回——该时段本机无声无听觉参照,显式接受。
/// 不钳 max(raw, target),因为发送端晶振慢时端上抽干缓冲的真实低占用必须可跟随。
///
/// target 是会话常量(深度变更走停播重启),经构造或 Reset 进入,只作初值,不进对齐臂。
/// 对齐臂基线是每拍传入的声明预算 D:对齐控制律把出声时刻钉在采集 + D,误差收敛后
/// 实际听觉滞后即 D;若拿 target 作基线,估计恒偏小约 D − target,歌词恒偏快。
/// D 非会话常量——MediaLinkAlignmentCoordinator 运行时重下发不经停播重启,
/// 构造缓存会粘住旧值,故走 Sample 逐拍传参。
/// 全成员收在一把私有锁内:采样与读频率不超过每 100ms 一次,零争用代价,
/// 不写无锁优化。
/// </summary>
internal sealed class OutputLatencyTracker
{
    /// <summary>死区宽度,毫秒。量化噪声峰峰约 20ms 之上、可察觉阈值 50ms 之半以下。</summary>
    internal const double DeadbandMs = 25;

    private readonly object _gate = new();
    private double _currentMs;

    /// <summary>初值即 target:未采到任何样本前,起播常量仍是最好的估计。</summary>
    internal OutputLatencyTracker(int requestedTargetMs) => Initialize(requestedTargetMs);

    /// <summary>当前输出延迟估计。与采样同一把锁,读到的恒为最近一次完整更新。</summary>
    internal TimeSpan Current
    {
        get
        {
            lock (_gate)
            {
                return TimeSpan.FromMilliseconds(_currentMs);
            }
        }
    }

    /// <summary>
    /// 喂入一拍渲染统计。顺序即规则:未起播先退,再选路,再死区,再一步跟随。
    ///
    /// 未起播时 stats 全零或瞬时读失败,维持现值兜底。选路:设备时钟偏移可用时
    /// 用对齐基线加对齐误差,不可用时退化读缓冲占用。
    ///
    /// 对齐臂基线取 alignedBaseMs(声明预算 D)而非 target:对齐控制律把出声时刻
    /// 钉在采集 + D,误差收敛到零时实际听觉滞后就是 D,基线加误差才是真实输出延迟。
    /// D 每拍传参:它运行时可变,MediaLinkAlignmentCoordinator 重下发不经停播重启,
    /// 会话内缓存读不到新值。
    ///
    /// 钳非负收在存储侧:负延迟无物理意义,而对齐误差在极端暂态可把 raw 推到负值。
    /// </summary>
    internal void Sample(
        bool hasStarted, bool clockOffsetAvailable, double ringMs, long playTimeErrorUs,
        double alignedBaseMs)
    {
        lock (_gate)
        {
            if (!hasStarted)
            {
                return;
            }

            var raw = clockOffsetAvailable ? alignedBaseMs + playTimeErrorUs / 1000.0 : ringMs;
            if (Math.Abs(raw - _currentMs) < DeadbandMs)
            {
                return;
            }

            _currentMs = Math.Max(raw, 0);
        }
    }

    /// <summary>回到与构造同语义的初值态。深度变更走停播重启,新 target 从这里进。</summary>
    internal void Reset(int requestedTargetMs) => Initialize(requestedTargetMs);

    /// <summary>构造与 Reset 共用的初值路径:初值语义只此一处。初值即 target。</summary>
    private void Initialize(int requestedTargetMs)
    {
        lock (_gate)
        {
            _currentMs = requestedTargetMs;
        }
    }
}
