using MediaIsland.Services.MediaLink.Protocol;

namespace MediaIsland.Services.MediaLink;

/// <summary>
/// 回程对时的时序外壳：什么时候探一次、连续没回应算不算失联、对端是不是根本不支持。
///
/// 时钟与收发都由外部注入，本类不碰 socket 也不读真实时间。理由与
/// <see cref="AudioClockOffsetEstimator"/> 相同：这一层真正难测的是「排队延迟突增」
/// 「应答迟到一拍」「对端只实现三个时刻」这些情形，它们在真实网络上极难复现，
/// 而注入之后每一种都是几行代码就能构造的。
///
/// 探测失败一律不抛也不断连。对时只服务于对齐播放，它不可用时上层退回缓冲深度控制，
/// media / lyrics / 转发三条线都不该因此受影响。
/// </summary>
internal sealed class AudioClockProbe
{
    /// <summary>
    /// 快速阶段的探测次数。刚连上时窗口是空的，估计值由唯一那个样本决定；
    /// 先密集探满一窗，此后再降到稳态周期。
    /// </summary>
    internal const int FastProbeCount = AudioClockOffsetEstimator.WindowSize;

    /// <summary>快速阶段周期。八次约 1.6 秒填满窗口。</summary>
    internal static readonly TimeSpan FastInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// 稳态周期。1 秒配合 8 样本窗口覆盖约 8 秒——而 8 秒内两台机器的单调时钟
    /// 相互漂移只有 0.08 毫秒，故整窗可当作同一个常量偏移来用。
    /// </summary>
    internal static readonly TimeSpan SteadyInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 连续多少次没有回应算失联。取 3 而非 1：单次丢包在无线链路上是常态，
    /// 而一次丢包就清空窗口会让 offset 反复进出可用状态，比偶尔用一个稍旧的估计更坏。
    /// </summary>
    internal const int MaxConsecutiveMisses = 3;

    private readonly Func<long> _now100Ns;
    private readonly Func<long, CancellationToken, Task<MediaLinkAudioClockPayload?>> _exchange;
    private readonly AudioClockOffsetEstimator _estimator = new();

    private int _accepted;
    private int _consecutiveMisses;

    internal AudioClockProbe(
        Func<long, CancellationToken, Task<MediaLinkAudioClockPayload?>> exchange,
        Func<long>? now100Ns = null)
    {
        _exchange = exchange ?? throw new ArgumentNullException(nameof(exchange));
        _now100Ns = now100Ns ?? Audio.MonotonicClock.Now100Ns;
    }

    /// <summary>
    /// 对端不支持四时间戳对时。与「暂时估不出」是两件事：前者永久，不必再探；
    /// 后者可自愈。上层要据此报不同的原因，否则用户看到的是同一句「没对齐」。
    /// </summary>
    internal bool IsUnsupported { get; private set; }

    /// <summary>无回应次数。累计量，供诊断。</summary>
    internal long Misses { get; private set; }

    /// <summary>回显的 t1 与请求不符的次数——迟到或重复的应答。</summary>
    internal long Mismatches { get; private set; }

    /// <summary>被估计器拒收的样本数，即往返为负的那些。</summary>
    internal long RejectedSamples { get; private set; }

    /// <summary>成功配成样本的次数。</summary>
    internal long AcceptedSamples => _accepted;

    /// <summary>
    /// 下一次探测该等多久。
    ///
    /// 用「已接受的样本数」而非「已发出的探测数」判断阶段：没配成样本时窗口仍是空的，
    /// 那时该继续密集探而不是因为发够了次数就降速。
    /// </summary>
    internal TimeSpan NextInterval => _accepted < FastProbeCount ? FastInterval : SteadyInterval;

    internal bool TryGetOffset(out long offsetTicks, out long chosenRoundTripTicks) =>
        _estimator.TryGetOffset(out offsetTicks, out chosenRoundTripTicks);

    /// <summary>
    /// 探一次。返回是否配成了一个可用样本。
    ///
    /// 任何失败都只反映在返回值与计数上，不抛。
    /// </summary>
    internal async Task<bool> ProbeOnceAsync(CancellationToken cancellationToken)
    {
        if (IsUnsupported)
        {
            return false;
        }

        var t1 = _now100Ns();
        MediaLinkAudioClockPayload? response;
        try
        {
            response = await _exchange(t1, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // 发不出去或等不到，与「回了但没内容」同等对待：都是这一次没成。
            response = null;
        }

        var t4 = _now100Ns();

        if (response is null)
        {
            NoteMiss();
            return false;
        }

        if (response.T2 is not { } t2 || response.T3 is not { } t3)
        {
            // 缺 t2 或 t3 即对端只实现了三时间戳。不退化成「假设处理耗时为零」——
            // 那会给出一个看起来可用的错值，比报不可用坏得多。
            IsUnsupported = true;
            _estimator.Reset();
            return false;
        }

        if (response.T1 != t1)
        {
            // 回显不符：这条应答配的是别的请求。拿它与本次的 t4 凑一个样本，
            // 往返会算成两次探测的间隔，是个大得离谱又看起来合法的数。
            Mismatches++;
            NoteMiss();
            return false;
        }

        if (!_estimator.TryAdd(new AudioClockSample(t1, t2, t3, t4)))
        {
            RejectedSamples++;
            return false;
        }

        _consecutiveMisses = 0;
        _accepted++;
        return true;
    }

    /// <summary>
    /// 按 <see cref="NextInterval"/> 持续探测，直到取消或确认对端不支持。
    ///
    /// 等待由外部传入而非直接 Task.Delay：判据要覆盖「快速阶段结束后降速」这类
    /// 时序性质，而那不该靠真的等上几秒。
    /// </summary>
    internal async Task RunAsync(
        Func<TimeSpan, CancellationToken, Task> delay, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delay);

        while (!cancellationToken.IsCancellationRequested && !IsUnsupported)
        {
            await ProbeOnceAsync(cancellationToken);
            if (IsUnsupported || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            await delay(NextInterval, cancellationToken);
        }
    }

    /// <summary>断连后清空。重连时对端可能已重启，其单调时钟零点已变。</summary>
    internal void Reset()
    {
        _estimator.Reset();
        _accepted = 0;
        _consecutiveMisses = 0;
    }

    private void NoteMiss()
    {
        Misses++;
        _consecutiveMisses++;
        if (_consecutiveMisses < MaxConsecutiveMisses)
        {
            return;
        }

        // 失联：丢掉整窗。留着旧样本会让上层在链路已断的情况下继续按一个
        // 越来越旧的 offset 对齐，而它的误差没有上界。
        _estimator.Reset();
        _accepted = 0;
    }
}
