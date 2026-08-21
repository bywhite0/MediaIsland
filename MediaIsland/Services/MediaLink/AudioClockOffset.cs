namespace MediaIsland.Services.MediaLink;

/// <summary>
/// 一次回程探测的四个时间戳，单位为 100 纳秒 tick。
///
/// T1 / T4 取自客户端单调时钟，T2 / T3 取自服务端单调时钟。四个缺一不可：
/// 只有 T1 / T3 / T4 就必须假设服务端处理耗时为零，而服务端一次 GC 或锁竞争
/// 就是几十毫秒，那个假设会把误差直接算进 offset 而不留痕迹。
///
/// 单位取 tick 而非毫秒：offset 估计的误差本身只有 0.5 毫秒量级，毫秒量化会污染它。
/// tick 与 TimeSpan.Ticks 一一对应，两侧都不需要换算代码。
///
/// 两侧的时钟都必须是单调时钟，不能是墙钟。墙钟会被 NTP slew 或 step 调整，
/// 而 offset 要跨分钟级持续维持——一次 step 就让此前所有样本失效，且无从察觉。
/// </summary>
internal readonly record struct AudioClockSample(long T1, long T2, long T3, long T4)
{
    /// <summary>客户端时刻加上本值即得服务端时刻。</summary>
    internal long OffsetTicks => ((T2 - T1) + (T3 - T4)) / 2;

    /// <summary>
    /// 往返耗时，已扣除服务端处理耗时。
    ///
    /// 物理上不可能为负。为负说明字段错位，或对端时钟在探测中途被重置。
    /// 那样的样本要丢弃而不是夹紧到零——夹紧会让一个坏样本看起来像最好的那一个，
    /// 而「最好的那一个」正是选择依据。
    /// </summary>
    internal long RoundTripTicks => (T4 - T1) - (T3 - T2);
}

/// <summary>
/// 从最近若干次探测里挑一个 offset。
///
/// 挑最小 RTT 那次，不取平均。理由是误差的方向：排队延迟只会让 RTT 变大，
/// 而它对 offset 的污染上界是 RTT/2。取最小者即取污染上界最小者；取平均则把
/// 所有样本的排队延迟都掺进结果，且一个离群值就能把它带偏。
///
/// 不做线性回归估 skew：对时用的是单调时钟（与 QPC 同源），两台机器间偏差约
/// 10ppm，8 秒窗口内漂移 0.08 毫秒——比估计噪声还小，估它是在拟合噪声。
/// 真正 200ppm 量级的偏差来自音频设备晶振，那个由渲染侧的控制律处理，不进这一层。
/// 把这两种时钟混起来，才会得出「需要卡尔曼滤波」的结论。
/// </summary>
internal sealed class AudioClockOffsetEstimator
{
    /// <summary>
    /// 窗口容量。8 次配合 1 秒探测周期覆盖约 8 秒，而 8 秒内单调时钟的相互漂移
    /// 只有 0.08 毫秒，可当作常量偏移；窗口再长就要开始考虑漂移，收益却只是
    /// 多几个候选样本。
    /// </summary>
    internal const int WindowSize = 8;

    /// <summary>
    /// RTT 上限。超过它则报不可用，而不是照样给一个 offset。
    ///
    /// 依据是误差预算：每端 5 毫秒，offset 那一项分到 0.5 毫秒，对应 RTT 约 1 毫秒。
    /// 取 50 毫秒作上限是留足余量后的兜底——RTT 到这个量级时 offset 误差可达 25 毫秒，
    /// 已经吃掉整个预算。此时退回缓冲深度控制比假装对齐正确。
    /// </summary>
    internal static readonly TimeSpan MaxAcceptableRoundTrip = TimeSpan.FromMilliseconds(50);

    private readonly AudioClockSample[] _window = new AudioClockSample[WindowSize];
    private int _count;
    private int _next;

    /// <summary>
    /// 收下一个样本，返回是否被收下。调用方据此记诊断——被拒的样本数是一个
    /// 值得看的量，它说明对端或链路有问题，而不只是「这次没估出来」。
    /// </summary>
    internal bool TryAdd(in AudioClockSample sample)
    {
        if (sample.RoundTripTicks < 0)
        {
            return false;
        }

        _window[_next] = sample;
        _next = (_next + 1) % WindowSize;
        if (_count < WindowSize)
        {
            _count++;
        }

        return true;
    }

    /// <summary>
    /// 取当前最可信的 offset。窗口为空、或最小 RTT 超上限时返回 false。
    ///
    /// 同时给出被选中样本的 RTT：「offset 是多少」与「这个 offset 可信到什么程度」
    /// 是两个量，只报前者会让排查无从下手，也让误差预算无从核对。
    /// </summary>
    internal bool TryGetOffset(out long offsetTicks, out long chosenRoundTripTicks)
    {
        offsetTicks = 0;
        chosenRoundTripTicks = 0;
        if (_count == 0)
        {
            return false;
        }

        var best = 0;
        for (var i = 1; i < _count; i++)
        {
            if (_window[i].RoundTripTicks < _window[best].RoundTripTicks)
            {
                best = i;
            }
        }

        if (_window[best].RoundTripTicks > MaxAcceptableRoundTrip.Ticks)
        {
            return false;
        }

        offsetTicks = _window[best].OffsetTicks;
        chosenRoundTripTicks = _window[best].RoundTripTicks;
        return true;
    }

    /// <summary>
    /// 断连后清空。留着旧样本会让重连后的第一个估计值来自上一条连接，而重连后的
    /// 对端可以是另一台机器——那时它的单调时钟零点属于另一台机器，旧 offset 是错值。
    ///
    /// 注意不是因为「对端进程重启使零点变了」：同一台机器上 QPC 零点是系统级的，
    /// 进程重启不改变它。要紧的是换了机器，不是换了进程。
    /// </summary>
    internal void Reset()
    {
        _count = 0;
        _next = 0;
    }
}
