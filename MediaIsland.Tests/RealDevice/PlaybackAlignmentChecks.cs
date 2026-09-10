using System.Diagnostics;
using MediaIsland.Services.Audio;
using MediaIsland.Services.Audio.Playback;
using MediaIsland.Services.Audio.Playback.Native;
using MediaIsland.Tests.Audio;
using MediaIsland.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace MediaIsland.Tests.RealDevice;

/// <summary>
/// 跨机对齐的真机判据。七条门控，逐条按它自己的核心断言记，条目间以分号分隔：
/// 稳态时刻误差进单端预算；设备会话事实被量到且对齐关闭时原路径不动；亚硬重置域
/// 重同步把占用剪回 target 带（硬重置恒零）；长闸域同样剪回 target 带（硬重置恰
/// 一次）；声明预算运行时下调后占用被当场剪到新 target 带；上调后占用被跨轮垫零
/// 垫到新 target 带且垫零帧不计欠载（硬重置恒零）；超额持续而盈余为零时外环残差
/// 不被清掉、目标深度因此持续下行。
///
/// 为何必须真机：误差信号的两个端点——IAudioClock::GetPosition 的位置/QPC 对与
/// 设备尾段延迟估计——只在真实 WASAPI 会话上存在。控制律本身已有仿真判据
/// （outer_loop.rs 的单测），仿真验不了的是「真设备上误差信号是活的、收敛真的发生」。
///
/// 单机立论：每端独立断言自己的稳态时刻误差 |e| 小于 5ms，两端都绿则由三角不等式
/// 保证两端差小于 10ms，且排除两端同向偏离目标的情形——这比两台对表更强，
/// 故数字判据不需要第二台机器，本机播放路径 + 对齐开启 + 本机对时就是测试环境。
///
/// 时长的物理推导（第一条判据的等待期由此定，不是拍的）：
/// 内环时间常数 τ内 = target_ms / 1000 / MAX_DRIFT，MAX_DRIFT = 0.001（ring.rs），
/// 故 τ内的秒数在数值上恰等于目标深度的毫秒数；目标深度取 native 下限 50ms 即
/// τ内约 50 到 80 秒（收敛期间深度上行）。外环积分时间是 3 倍 τ内，该闭环欠阻尼；
/// 实测收敛尾部的有效时间常数约 210 秒。等待期为判稳早退加硬上限的形式，
/// 数值推导见 SteadyWaitSeconds 与 SettledProbeMs 的注释。若 native 下限或
/// MAX_DRIFT 变了，这里的推导失真，表现是判据不稳（可见），不是假绿——
/// 下限另有断言钉住。
/// </summary>
[Collection(nameof(RealDeviceCollection))]
[Trait("RealDevice", "Slow")]
public class PlaybackAlignmentChecks(ITestOutputHelper output)
{
    private const double ToneHz = 440;
    private const double Amplitude = 0.5;

    /// <summary>100ns tick 每毫秒。</summary>
    private const long TicksPerMs = 10_000;

    /// <summary>单端稳态误差预算。两端差 10ms 的一半，三角不等式的分配额。</summary>
    private const double PerEndBudgetMs = 5.0;

    /// <summary>
    /// 校准后注入的初始误差，毫秒。取负方向（实际出声时刻早于目标）：外环要抬高
    /// 目标深度去追，向上远离 MIN 夹紧边界；正方向在下限深度起播时立即撞夹紧，
    /// 外环没有行程。20ms 远大于死区（1ms）与预算（5ms），保证「外环停掉必红」
    /// 与「等待缩短必红」都是构造性的，不靠运气。
    /// </summary>
    private const double InjectedErrorMs = 20.0;

    /// <summary>
    /// 校准前的发送端延迟预算初值，毫秒。量级取「下限深度 + 设备尾段」的估计，
    /// 让校准期误差不至于大到把外环推上速率上限（那会在校准窗内拖着目标深度走）。
    /// 精确值无所谓——校准量出它下面的误差中位，注入量按那个中位折算，而 D 本身
    /// 全程不动（不拨 D 的理由住在注入点一处）。
    /// </summary>
    private const int InitialDelayBudgetMs = 80;

    /// <summary>
    /// 稳态等待的硬上限，秒。收敛时间 ∝ 初始偏差 × τ，而两者逐跑漂移：会话级链路
    /// 延迟在校准后仍可再漂几毫秒（把有效初始偏差推到 ~25ms），且实测收敛尾部的
    /// 有效时间常数约 210 秒，慢于欠阻尼包络的 2τ内 估计（二阶系统初始斜率为零，
    /// 早期进度被高估）。固定等待编码的是这个分布的一个点估计——曾取 330 秒，
    /// 四次实跑落点 -0.8/-2.7/-3.6/-5.3ms，最后一次撞线出界。形式改成判稳早退 +
    /// 硬上限：上限按注入确认带最坏边 28ms 推，28 × exp(-480/230) ≈ 3.5ms，
    /// 仍在 5ms 预算内；典型跑由早退保住时长。指数里的 230 是实测约 210 秒加垫——
    /// 衰减估计里 τ 取大是保守方向，垫住逐跑漂移。
    /// </summary>
    private const int SteadyWaitSeconds = 480;

    /// <summary>
    /// 判稳早退的探针阈值，毫秒。连续三次 15 秒探针 |e| 低于它才算进稳态——
    /// 单次低读数可能是抖动路过零点。取预算的一半，早退后窗口中位必然远离判据线。
    /// 探针读数恰为 0 不算数，且清零连击：死信号读 0，若拿它当已收敛就把活性锚绕过去了。
    /// </summary>
    private const double SettledProbeMs = 2.5;

    /// <summary>稳态采样窗，秒。取分位数而非瞬时值，10Hz 采样约 200 个样本。</summary>
    private const int WindowSeconds = 20;

    /// <summary>
    /// 声明预算起播取值相对目标深度的加项，毫秒。链路里目标深度之外还有两段：设备
    /// 缓冲里压着的 padding（本机缓冲 1056 帧即 22ms，压着的是它的一部分）与设备
    /// 尾段延迟估计（本机实测 10ms）。
    ///
    /// D 取「目标深度 + 本项」使出声时刻误差约零。「约零」买到的两件事都不要它精确：
    /// 重同步门不点火（超额阈 250ms，本机实测基线在 30ms 以内，离它一个数量级），
    /// 以及外环走不动多少（误差偏 20ms 时约 0.03 毫秒每秒，判据窗内不到零点几毫秒
    /// —— 不是落进 1ms 死区，是慢到看不见）。
    /// </summary>
    private const int LinkTailEstimateMs = 20;

    [RealAudioFact]
    public async Task SteadyStateAlignmentError_StaysWithinPerEndBudget()
    {
        AudioRenderNative.ResetForTesting();

        using var renderer = new WasapiRenderer();
        Assert.True(renderer.IsAvailable, $"播放不可用：{renderer.FailureReason}");

        var played = new PlayedPcmRecorder();
        using var playback = new AudioPlaybackService(played, renderer);

        var (minMs, maxMs) = WasapiRenderer.TargetMsBounds();
        var targetMs = (int)minMs;

        // 时长推导建立在下限约 50ms 上。下限若变大，τ内跟着变大，330 秒不再够——
        // 让它在这里显式红掉并指回推导，比一条开始随机失败的判据好查得多。
        Assert.True(minMs <= 80, $"native 目标深度下限已变为 {minMs}ms，稳态等待的时长推导需重推");
        Assert.True(maxMs >= minMs + 50, $"深度区间 [{minMs},{maxMs}] 给外环的上行行程不足");

        // 对齐参数在本实例首次 Start 之前下发——这正是曾被零句柄丢弃缺陷吞掉的
        // 自然路径（渲染器暂存参数、起播时先补发再 render_start），本判据即其回归判据：
        // 丢弃回归时 offset 永远不可用，下面的地基断言先红。
        //
        // offset 传 1 tick 而不是 0：本机对时的真实 offset 就是零，但 native 侧
        // offset_available 用「非零」表示可用，传 0 等于宣告对时结果还没到，外环不走。
        // 1 tick = 100ns，对毫秒级判据是零。
        playback.ConfigureAlignment(
            enabled: true,
            dTicks: InitialDelayBudgetMs * TicksPerMs,
            offsetTicks: 1,
            manualOffsetTicks: 0);
        playback.Configure(enabled: true, targetBufferMs: targetMs);
        Assert.True(playback.IsPlaying, $"起播失败：{playback.LastError}");

        // 发送端时间戳内容线性，不用注入泵的墙钟：追赶式泵成簇发块，簇内各块拿到
        // 同一个「现在」，锚点会带上十几毫秒的锯齿。恒定的基准差由校准吸收。
        var stamper = new SenderTimelineStamper();
        using var sine = new SineFrameSource(ToneHz, Amplitude);
        sine.FrameAvailable += frame => playback.Submit(stamper.Stamp(frame));
        await sine.StartAsync(CancellationToken.None);

        // 预填充与起播过渡走完再看。
        await Task.Delay(3_000);

        var foundation = renderer.ReadStats();
        Assert.True(foundation.HasStarted, "统计说从未起播");
        Assert.True(
            foundation.DeviceClockAvailable,
            "端点无 IAudioClock：位置锚点不产生，对齐在本端点无从进行");
        Assert.True(foundation.ClockOffsetAvailable, "offset 未被判可用：外环不会走步");
        Assert.True(foundation.RingFrames > 0, "占用为零：帧没进到环形缓冲");

        // 振幅地基先于一切时刻断言（此处及收尾各一次）：静音端点上时刻误差恒为零
        // 也会绿，那正是本期要修掉的缺陷形态。这里扫的是已播出帧——时刻判据所断言的
        // 正是这些帧的出声时刻，比 loopback 采回的耦合更紧。
        var earlyPlayed = played.Snapshot();
        Assert.True(earlyPlayed.Count > 0, "FramePlayed 一次都没触发");
        Assert.True(
            earlyPlayed.Peak >= AudioAlignmentThresholds.PeakAmplitudeLowerBound,
            $"已播出峰值 {earlyPlayed.Peak} 低于下界 {AudioAlignmentThresholds.PeakAmplitudeLowerBound}，注入疑似静音");
        Assert.True(
            NonZeroRatio(earlyPlayed) >= AudioAlignmentThresholds.NonZeroFrameRatioLowerBound,
            $"已播出非零帧占比 {NonZeroRatio(earlyPlayed):F2} 低于下界");

        // 校准：量出误差中位数，把 D 拨到误差约零的位置。未知的固定链路延迟
        // （缓冲深度 + 设备尾段 + 时间戳基准差）从收敛路径中消失，测试时长从此
        // 只依赖控制律的时间常数，不依赖本机链路有多长。
        var calibration = await SampleErrorUsAsync(renderer, seconds: 10);
        var calibrationLive = calibration.Where(e => e != 0).ToList();
        Assert.True(calibrationLive.Count >= 50, $"校准窗内活样本仅 {calibrationLive.Count} 个：误差信号没在产");
        var rawErrorMs = MedianMs(calibrationLive);
        Assert.True(Math.Abs(rawErrorMs) < 150, $"校准误差 {rawErrorMs:F1}ms 离谱：链路延迟异常或时间轴断裂");

        var measuredLatencyMs = InitialDelayBudgetMs + rawErrorMs;
        output.WriteLine($"校准误差中位    : {rawErrorMs:F2} ms（D 预算 {InitialDelayBudgetMs}ms 下）");
        output.WriteLine($"实测发送到出声  : {measuredLatencyMs:F2} ms（下限深度 {targetMs}ms + 设备尾段）");

        // 注入已知初始误差，走手动偏移而不是 D。手动偏移加在实际出声那一侧（补自动
        // 估计没看见的那段硬件延迟），负值即实际出声更早，误差瞬间变为约 -20ms，
        // 而只有外环能把它修掉。
        //
        // 不能拿 D 注入：声明预算的阶跃执行器会把目标深度按 ΔD 走满、占用随之跟到位，
        // 于是出声时刻相对发送端时刻按新预算落位，误差被前馈整量抵消回原值——本机实测
        // 拨 +10.8ms 的 D 之后误差从 -9.2 回到 -10.5，注入等于没发生。
        //
        // 拿 D 注入只在两种情形下还能成立，两种都不能用：变化量落在阶跃死区以下
        // （抵消不发生，但注入量也就不到一毫秒），或目标深度已撞界使生效量被夹成 0
        // （而这里起播就在下界上，注入的正负方向决定它撞不撞，那是靠本机链路延迟的
        // 符号赌一把）。手动偏移不被阶跃检测读（它的锚只跟 D 走），故执行器不点火，
        // 注入无条件成立，「只有外环能把它修掉」这条立论也仍然成立。
        var injectedManualTicks =
            (long)Math.Round((-InjectedErrorMs - rawErrorMs) * TicksPerMs);
        playback.ConfigureAlignment(
            true,
            InitialDelayBudgetMs * TicksPerMs,
            offsetTicks: 1,
            manualOffsetTicks: injectedManualTicks);
        await Task.Delay(1_000);

        var injected = await SampleErrorUsAsync(renderer, seconds: 3);
        var injectedMs = MedianMs(injected);
        output.WriteLine($"注入后误差中位  : {injectedMs:F2} ms（期望约 -{InjectedErrorMs:F0}）");
        // 这条同时是「误差信号活着」的锚：信号死了读数是 0，落不进这个带。
        Assert.InRange(injectedMs, -InjectedErrorMs - 8, -InjectedErrorMs + 8);

        // 稳态等待：判稳早退 + 硬上限，推导见两常量注释。期间记轨迹供诊断，
        // 不断言——断言全部收在窗口采样之后。
        var trajectory = Stopwatch.StartNew();
        var settledProbes = 0;
        while (trajectory.Elapsed.TotalSeconds < SteadyWaitSeconds)
        {
            await Task.Delay(15_000);
            var probe = renderer.ReadStats();
            output.WriteLine(
                $"t+{trajectory.Elapsed.TotalSeconds,5:F0}s : e={probe.PlayTimeErrorUs,7}us target={probe.TargetMsCurrent}ms ring={probe.RingMs:F0}ms 欠载={probe.UnderrunCount}");

            var isSettledProbe = probe.PlayTimeErrorUs != 0
                && Math.Abs(probe.PlayTimeErrorUs) < SettledProbeMs * 1_000;
            settledProbes = isSettledProbe ? settledProbes + 1 : 0;
            if (settledProbes >= 3)
            {
                break;
            }
        }

        var window = await SampleErrorUsAsync(renderer, WindowSeconds);
        var steady = renderer.ReadStats();
        var playedFinal = played.Snapshot();

        window.Sort();
        var p10 = window[window.Count / 10] / 1_000.0;
        var p90 = window[window.Count * 9 / 10] / 1_000.0;
        var medianErrorMs = MedianMs(window);
        output.WriteLine($"稳态窗口        : {window.Count} 样本，e 中位 {medianErrorMs:F2}ms，p10 {p10:F2}ms，p90 {p90:F2}ms");
        output.WriteLine($"目标深度        : 起播 {targetMs}ms → 当前 {steady.TargetMsCurrent}ms");
        output.WriteLine($"欠载 / 硬重置   : {steady.UnderrunCount} / {steady.HardResetCount}（对齐开启即建重采样器的路径）");
        output.WriteLine($"已播出          : {playedFinal.Count} 帧，峰值 {playedFinal.Peak}，非零占比 {NonZeroRatio(playedFinal):F2}");

        // 顺序是纪律不是风格：先振幅，再时刻误差。
        Assert.True(
            playedFinal.Peak >= AudioAlignmentThresholds.PeakAmplitudeLowerBound,
            $"已播出峰值 {playedFinal.Peak} 低于下界 {AudioAlignmentThresholds.PeakAmplitudeLowerBound}，注入疑似静音");
        Assert.True(
            NonZeroRatio(playedFinal) >= AudioAlignmentThresholds.NonZeroFrameRatioLowerBound,
            $"已播出非零帧占比 {NonZeroRatio(playedFinal):F2} 低于下界");

        // 误差信号中途死掉（offset 失可用、时钟失联）时读数归零，中位会被拽向 0，
        // 那是假绿——先钉信号活着，再看数值。
        var liveSamples = window.Count(e => e != 0);
        Assert.True(liveSamples * 10 >= window.Count * 9, $"稳态窗内活样本仅 {liveSamples}/{window.Count}：误差信号中途死掉");

        // 门控核心：单端稳态误差进 5ms。
        Assert.True(
            Math.Abs(medianErrorMs) < PerEndBudgetMs,
            $"稳态时刻误差中位 {medianErrorMs:F2}ms 超出单端预算 {PerEndBudgetMs}ms：两端差进 10ms 的立论不成立");

        // 外环真的在写 target_ms 的真判据（此前只有直接喂值的 stats 单测）：
        // 注入的 20ms 只能靠深度移动去执行，下界取一半（余量给校准误差与死区），
        // 上界挡住过冲游走。
        Assert.True(
            steady.TargetMsCurrent >= targetMs + 10 && steady.TargetMsCurrent <= targetMs + 35,
            $"target_ms_current = {steady.TargetMsCurrent}（起播 {targetMs}）：外环没在写它，或走飞了");

        // 稳态阶段不许硬重置；欠载计数照 PlaybackDriftChecks 先例只记录不设零阈
        // （调度抖动会造成偶发），数值进输出供台账。
        Assert.Equal(0, steady.HardResetCount);

        // 硬重置后 prefill 重建到外环当下的 target_ms，而不是起播时冻结的值。
        // 欠载可注入：停喂即欠载，累积过 500ms 触发恰一次硬重置。
        await sine.StopAsync(CancellationToken.None);
        await Task.Delay(1_500);

        var afterStall = renderer.ReadStats();
        Assert.Equal(1, afterStall.HardResetCount);
        var targetNow = afterStall.TargetMsCurrent;

        var resumeStamper = new SenderTimelineStamper();
        using var resumed = new SineFrameSource(ToneHz, Amplitude);
        resumed.FrameAvailable += frame => playback.Submit(resumeStamper.Stamp(frame));
        await resumed.StartAsync(CancellationToken.None);
        await Task.Delay(2_500);

        var occupancy = await SampleRingMsAsync(renderer, seconds: 3);
        output.WriteLine($"重置后占用中位  : {occupancy:F1} ms（外环当下 {targetNow}ms，起播冻结 {targetMs}ms）");

        // 中点判别：重建到当下则占用贴 targetNow；重建到冻结值则贴 targetMs，
        // 内环把差额追回来要一个 τ内（约一分钟），三秒窗内爬不过 1ms，两态分得开。
        var midpoint = targetMs + (targetNow - targetMs) / 2.0;
        Assert.True(
            occupancy > midpoint,
            $"重置后占用中位 {occupancy:F1}ms 未过中点 {midpoint:F1}ms：prefill 重建用的是起播冻结的深度");

        // 连续欠载只换一次硬重置，恢复后不再重置。
        Assert.Equal(1, renderer.ReadStats().HardResetCount);

        await resumed.StopAsync(CancellationToken.None);
        playback.Configure(enabled: false, targetBufferMs: targetMs);
    }

    [RealAudioFact]
    public async Task DeviceSessionFacts_AreMeasuredAndClosedPathStaysUntouched()
    {
        AudioRenderNative.ResetForTesting();

        using var renderer = new WasapiRenderer();
        Assert.True(renderer.IsAvailable, $"播放不可用：{renderer.FailureReason}");

        var played = new PlayedPcmRecorder();
        using var playback = new AudioPlaybackService(played, renderer);

        // 对齐从未开启地起播：这一段钉「关闭走原路径」。帧刻意带发送端时间戳——
        // 关闭路径的承诺是「即使时刻在场也不走时间轴」，不带时刻的验证是弱形。
        const int targetMs = 200;
        playback.Configure(enabled: true, targetBufferMs: targetMs);
        Assert.True(playback.IsPlaying, $"起播失败：{playback.LastError}");

        var stamper = new SenderTimelineStamper();
        using var sine = new SineFrameSource(ToneHz, Amplitude);
        sine.FrameAvailable += frame => playback.Submit(stamper.Stamp(frame));
        await sine.StartAsync(CancellationToken.None);
        await Task.Delay(3_000);

        var closed = renderer.ReadStats();
        var earlyPlayed = played.Snapshot();

        output.WriteLine($"设备采样率      : {closed.DeviceSampleRate}");
        output.WriteLine($"设备延迟实测    : {closed.DeviceLatencyUs} us = {closed.DeviceLatencyUs / 1_000.0:F3} ms（引擎周期 + 流延迟附加项）");
        output.WriteLine($"设备缓冲        : {closed.DeviceBufferFrames} 帧 = {(closed.DeviceSampleRate > 0 ? closed.DeviceBufferFrames * 1_000.0 / closed.DeviceSampleRate : 0):F1} ms");
        output.WriteLine($"设备时钟可用    : {closed.DeviceClockAvailable}");

        Assert.True(closed.HasStarted, "统计说从未起播");

        // 振幅先行，纪律同第一条。
        Assert.True(earlyPlayed.Count > 0, "FramePlayed 一次都没触发");
        Assert.True(
            earlyPlayed.Peak >= AudioAlignmentThresholds.PeakAmplitudeLowerBound,
            $"已播出峰值 {earlyPlayed.Peak} 低于下界 {AudioAlignmentThresholds.PeakAmplitudeLowerBound}，注入疑似静音");
        Assert.True(
            NonZeroRatio(earlyPlayed) >= AudioAlignmentThresholds.NonZeroFrameRatioLowerBound,
            $"已播出非零帧占比 {NonZeroRatio(earlyPlayed):F2} 低于下界");

        // 门控核心：设备延迟估计只有真设备才有值。已落定裁决：引擎周期承担已知部分
        // （本机实测默认 10ms、最小 2 到 3ms），GetStreamLatency 有值才加——
        // 故真设备上它必为正，且不该超出共享模式合理量级两个数量级。
        Assert.InRange(closed.DeviceLatencyUs, 1_000, 100_000);
        Assert.True(closed.DeviceBufferFrames > 0, "设备缓冲容量为零：GetBufferSize 没取到");

        // 对齐关闭时 GetPosition 不调：位置两字段保持 0。这同时钉住全局约束
        // 「关闭走原路径」——位置锚点那一段在关闭时不存在。
        Assert.Equal(0, closed.DevicePositionFrames);
        Assert.Equal(0, closed.DevicePositionQpc);
        // 误差与目标深度同理：关闭时误差恒为 0（此处 0 的含义是「算不出」，由
        // ClockOffsetAvailable=false 区分），目标深度恒等于起播请求值。
        Assert.Equal(0, closed.PlayTimeErrorUs);
        Assert.Equal(targetMs, closed.TargetMsCurrent);
        Assert.False(closed.ClockOffsetAvailable, "对齐从未开启，offset 不该被判可用");

        // 闸住性质的端到端回栓：运行时三项播放中随时可下发（offset 传非零——对时
        // 结果到了就会走这条路），但会话起播时对齐是关的，native 的可用性判定被
        // 启用位闸住：offset 不判可用、锚点不产生、误差不算、外环不动。这同时是
        // 「同值零重启」的 false 侧——enabled 同为关绝不触发重启。判别取会话
        // 起播值：它保持 false 即无携带 true 的重启。对「重启后记回同为 false」
        // 的重启它是盲的（StartUnlocked 记回的还是 false），而帧水位在关闭会话
        // 里同样分不开两态（三元组前后同为零），此处不再另设判别。
        playback.ConfigureAlignment(
            enabled: false, dTicks: 500 * TicksPerMs, offsetTicks: 1, manualOffsetTicks: 0);
        await Task.Delay(1_500);

        var gated = renderer.ReadStats();
        Assert.False(playback.SessionAlignmentEnabled, "同值（关）下发不该触发重启改写会话起播值");
        Assert.False(
            gated.ClockOffsetAvailable,
            "关闭会话里下发非零 offset 不该被判可用：启用位的闸门漏了");
        Assert.Equal(0, gated.DevicePositionFrames);
        Assert.Equal(0, gated.DevicePositionQpc);
        Assert.Equal(0, gated.PlayTimeErrorUs);
        Assert.Equal(targetMs, gated.TargetMsCurrent);

        // ABI 5 起对齐开关是起播参数，native 会话内不可变——而托管侧的生效方式是
        // Task 4 的新契约：播放中拨开关，ConfigureAlignment 比对会话起播值，不一致
        // 即恰一次停播重启，新会话携带新开关；同值绝不重启（对时结果每秒下发走的
        // 就是这条路）。第 7 期「误差三要素齐备而执行器缺席」的 windup 形态在类型上
        // 仍写不出来；本段钉新契约在真机上的可观测面：切换后会话起播值跟随、新会话
        // 的对齐路径真的活了（offset 判可用、位置锚点产生、误差在算）、深度取暂存
        // 请求值而非默认值、重启是干净的（无硬重置、声还在出）、同值再下发零重启。
        playback.ConfigureAlignment(
            enabled: true, dTicks: 500 * TicksPerMs, offsetTicks: 1, manualOffsetTicks: 0);
        await Task.Delay(3_000);

        var armed = renderer.ReadStats();
        var playedAfterFlip = played.Snapshot();
        output.WriteLine($"切换后误差      : {armed.PlayTimeErrorUs} us（D 拨在 500ms 上，链路远短于它）");
        output.WriteLine($"切换后目标深度  : {armed.TargetMsCurrent} ms（起播请求 {targetMs}ms）");
        output.WriteLine($"切换后位置锚点  : {armed.DevicePositionFrames} 帧 / QPC {armed.DevicePositionQpc}");

        // 会话起播值跟随为开：重启真的发生且携带了新开关。这一条红意味着中途切换
        // 没有生效——用户拨了开关、设置页显示为开，而本会话仍在关闭路径上出声。
        Assert.True(
            playback.SessionAlignmentEnabled,
            "播放中拨开开关后会话起播值未跟随：中途切换的停播重启没有发生");
        Assert.True(playback.IsPlaying, $"切换重启后不在播：{playback.LastError}");
        Assert.True(armed.HasStarted, "切换重启后统计说从未起播");
        // 干净的重启不是错误路径：硬重置属于欠载恢复，切换不该借道它。
        Assert.Equal(0, armed.HardResetCount);

        // 振幅先行，纪律同上：先证明重启后声音还在出，再轮到时刻断言。
        Assert.True(
            playedAfterFlip.Count > earlyPlayed.Count,
            "切换重启后已播出帧数没有增长：重启后没再出声");
        Assert.True(
            playedAfterFlip.Peak >= AudioAlignmentThresholds.PeakAmplitudeLowerBound,
            $"切换后已播出峰值 {playedAfterFlip.Peak} 低于下界，重启后疑似静音");

        // 新会话的对齐路径活了。位置锚点只在对齐会话里产生（GetPosition 那一段
        // 在关闭会话里不存在，上面 closed 段刚断过恒零），故这三条合起来就是
        // 「新会话真的带着开关起来了」的实测形态。
        Assert.True(
            armed.DeviceClockAvailable,
            "端点无 IAudioClock：位置锚点不产生，本段判据在此端点无从进行");
        Assert.True(armed.ClockOffsetAvailable, "新会话携带开关起播，offset 该被判可用");
        Assert.True(armed.DevicePositionFrames > 0, "位置锚点未产生：GetPosition 没被调用");
        Assert.True(armed.DevicePositionQpc > 0, "位置锚点无 QPC：锚点对不完整");
        // D 拨在 500ms 上而实际链路远短于它（200ms 深度 + 设备尾段），误差必为
        // 显著负值——非零钉信号活着，负号钉方向约定（第一条判据实测过正 D 得负误差）。
        Assert.True(
            armed.PlayTimeErrorUs < 0,
            $"切换后误差 {armed.PlayTimeErrorUs}us 不为负：新会话的误差没在算");

        // 深度取暂存请求值：重启不该顺手改掉用户的 200ms。外环速率上限 0.5ms/s，
        // 三秒窗内至多走 2ms，上界的余量给它 5ms。
        Assert.InRange(armed.TargetMsCurrent, targetMs, targetMs + 5);

        // 同值再下发绝不再重启——对时结果每秒下发走的就是这条路，重启风暴的
        // 形态就是它。判别用位置锚点跨探针单调：新会话的 IAudioClock 位置从零
        // 重数，上一窗已积累约三秒，探针只等一秒，若发生了第二次重启，读数
        // 到不了上一窗的水位。
        playback.ConfigureAlignment(
            enabled: true, dTicks: 480 * TicksPerMs, offsetTicks: 1, manualOffsetTicks: 0);
        await Task.Delay(1_000);
        var afterSameValue = renderer.ReadStats();
        output.WriteLine($"同值下发后锚点  : {afterSameValue.DevicePositionFrames} 帧（切换后水位 {armed.DevicePositionFrames}）");
        Assert.True(
            afterSameValue.DevicePositionFrames > armed.DevicePositionFrames,
            $"同值下发后位置锚点 {afterSameValue.DevicePositionFrames} 未越过切换后水位 {armed.DevicePositionFrames}：会话疑似被再次重启");

        await sine.StopAsync(CancellationToken.None);
        playback.Configure(enabled: false, targetBufferMs: targetMs);
    }

    /// <summary>
    /// 亚硬重置域重同步的真机门控：500ms 闸回灌形成的占用棘轮被重同步门剪除，
    /// 且 OutputLatency 双向跟随（冲高一步跟上、剪除一步回落）。与长闸判据
    /// LongStallBackfill_ResyncTrimsOccupancyToTargetBand 互补：那边欠载累计越
    /// 500ms 阈（硬重置恰一次）加容量封顶，这边欠载累计约 300ms 低于阈——
    /// HardResetCount 恒 0 即两域的判别锚，剪积压全靠重同步门，不借道硬重置。
    ///
    /// 真实病灶是网络卡顿：发送端持续产帧，帧被延迟后整批补达。接收端 ring 先耗尽
    /// target 时长的真实帧、再垫零，积压到达后占用冲到约卡顿时长的水平。第 10 期
    /// 本判据锚的是该水平「维持」（430–570，当时 FIFO 确无拉回路径）并被
    /// OutputLatency 跟随；第 11 期重同步门落地（超额大于 250ms 持续 500ms 即
    /// 丢最旧收干回 target），该场景确定性剪回 target 带，「维持」锚随第 11 期
    /// 设计废止，本判据重锚为剪除后的终态带，冲高侧跟随（第 10 期交付）降为
    /// 点火前的短轮询探针继续在场。
    ///
    /// 注入必须闸住缓存、到点整批回灌，不能停源再重启：停掉的源不产生积压，
    /// 恢复后占用趋零，方向就反了，测不到病灶。
    ///
    /// 数值账：对齐关（FIFO，默认病灶路径），target 200ms。闸 500ms：ring 先耗掉约
    /// 200ms 真实帧，再垫约 300ms 零（UnderrunCount 大于 0）；欠载累计约 300ms 低于
    /// 500ms 硬重置阈（第 9 期判据：累积过 500ms 触发恰一次硬重置）→ HardResetCount
    /// 恒 0。回灌约 500ms 积压 → 占用冲到约 500ms，超额约 300ms 大于 250ms 阈，
    /// 持续 500ms（24_000 消费帧）后重同步点火剪回 target 带；OutputLatency（节流
    /// 100ms + 死区 25ms）冲高后一步跟到占用水平（点火前探针钉住），剪除后同步回落。
    /// </summary>
    [RealAudioFact]
    public async Task NetworkStallBackfill_ResyncTrimsOccupancyWithoutHardReset()
    {
        AudioRenderNative.ResetForTesting();

        using var renderer = new WasapiRenderer();
        Assert.True(renderer.IsAvailable, $"播放不可用：{renderer.FailureReason}");

        var played = new PlayedPcmRecorder();
        using var playback = new AudioPlaybackService(played, renderer);

        // 对齐不开启（不调 ConfigureAlignment），比对 DeviceSessionFacts 的关闭路径开场。
        // 帧照带发送端时间戳——关闭路径承诺「时刻在场也不走时间轴」的强形。
        const int targetMs = 200;

        // 卡顿时长。垫零约 500 - 200 = 300ms，离 500ms 硬重置阈有 200ms 的构造性余量
        // （亚硬重置域不擦线）；回灌约 500ms → 超额约 300ms 越 250ms 重同步阈，且
        // 持续期内消费与直送流入相抵、超额不衰减——点火同样构造性成立，不靠运气。
        const int stallMs = 500;

        // 跟随容差：死区 25 + 采集块 10 + 节流陈旧余量。吞不掉 300ms 的病灶量级。
        const double followToleranceMs = 50;

        playback.Configure(enabled: true, targetBufferMs: targetMs);
        Assert.True(playback.IsPlaying, $"起播失败：{playback.LastError}");

        // 喂帧闸：常态直送，闸下入队缓存，开闸先按序整批回灌再恢复直送。
        // Submit 收在锁内，回灌期间泵线程在锁上等，直送不会插进回灌序列。
        var gate = new object();
        var gateClosed = false;
        var held = new List<AudioFrame>();
        var stamper = new SenderTimelineStamper();
        using var sine = new SineFrameSource(ToneHz, Amplitude);
        sine.FrameAvailable += frame =>
        {
            lock (gate)
            {
                var stamped = stamper.Stamp(frame);
                if (gateClosed)
                {
                    held.Add(stamped);
                    return;
                }

                playback.Submit(stamped);
            }
        };
        await sine.StartAsync(CancellationToken.None);

        // 预填充与起播过渡走完再看。
        await Task.Delay(3_000);

        // 振幅地基先于一切时刻断言，纪律同上两条。
        var earlyPlayed = played.Snapshot();
        Assert.True(earlyPlayed.Count > 0, "FramePlayed 一次都没触发");
        Assert.True(
            earlyPlayed.Peak >= AudioAlignmentThresholds.PeakAmplitudeLowerBound,
            $"已播出峰值 {earlyPlayed.Peak} 低于下界 {AudioAlignmentThresholds.PeakAmplitudeLowerBound}，注入疑似静音");
        Assert.True(
            NonZeroRatio(earlyPlayed) >= AudioAlignmentThresholds.NonZeroFrameRatioLowerBound,
            $"已播出非零帧占比 {NonZeroRatio(earlyPlayed):F2} 低于下界");

        Assert.True(renderer.ReadStats().HasStarted, "统计说从未起播");

        // 基线：FIFO 稳态占用贴 target；OutputLatency 该在占用的跟随容差内。
        // 占用带不动摇后面的推导（垫零量与硬重置余量从基线占用出发；回填水位
        // ≈ 闸时长，与基线近似无关），先钉住它。
        var baselineRingMs = await SampleRingMsAsync(renderer, seconds: 3);
        var baselineLatencyMs = playback.OutputLatency.TotalMilliseconds;
        output.WriteLine($"基线占用中位    : {baselineRingMs:F1} ms（target {targetMs}ms）");
        output.WriteLine($"基线输出延迟    : {baselineLatencyMs:F1} ms");
        Assert.InRange(baselineRingMs, targetMs - 50, targetMs + 50);
        Assert.True(
            Math.Abs(baselineLatencyMs - baselineRingMs) <= followToleranceMs,
            $"基线 |OutputLatency {baselineLatencyMs:F1} − 占用中位 {baselineRingMs:F1}| 超 {followToleranceMs}ms");

        // 闸 500ms：泵线程照常产帧入 held，ring 先耗真实帧再垫零。
        lock (gate)
        {
            gateClosed = true;
        }

        await Task.Delay(stallMs);

        // 开闸整批回灌：占用回填到约「卡顿时长」的水平。
        lock (gate)
        {
            foreach (var frame in held)
            {
                playback.Submit(frame);
            }

            held.Clear();
            gateClosed = false;
        }

        var atBackfill = played.Snapshot();

        // 回灌探针：积压真的进了 ring。没有这一条，终态的「占用回 target 带」与
        // 「积压根本没进来」不可区分——修与不修都该绿的前提要单独钉住。
        // RingMs 是渲染轮快照（每轮 set_ring_frames），回灌完成到下一轮之间隔着
        // 一个消费节拍，零延迟单次读会撞上回灌前的旧快照——故短轮询。预算 300ms
        // 远小于点火时刻（超额持续 500ms 从回灌起算），读到高水位时重同步必未点火，
        // 语义是「点火前进了 ring」；积压真没进来时轮询同样读不到，判别力不丢。
        var ringProbeClock = Stopwatch.StartNew();
        var backfillProbeMs = renderer.ReadStats().RingMs;
        while (backfillProbeMs <= 400 && ringProbeClock.ElapsedMilliseconds < 300)
        {
            await Task.Delay(10);
            backfillProbeMs = renderer.ReadStats().RingMs;
        }

        output.WriteLine($"回灌探针占用    : {backfillProbeMs:F1} ms（闸 {stallMs}ms，探针窗 {ringProbeClock.ElapsedMilliseconds}ms）");
        Assert.True(
            backfillProbeMs > 400,
            $"回灌后占用探针 {backfillProbeMs:F1}ms 未过 400ms：积压没进 ring，终态断言失去前提");

        // 上行跟随探针（第 10 期交付的保全）：占用冲高后 OutputLatency 一步跟上。
        // 节流 100ms 加死区 25ms 远小于 300ms 预算；此窗仍在点火前（时序同上一条）。
        // 上行方向的跟随证据只在这里存在——终态时占用已剪回 target，恒回起播常量的
        // 退化实现也能贴上终态跟随断言，冲不过这里的 400ms。
        var latencyProbeClock = Stopwatch.StartNew();
        var probeLatencyMs = playback.OutputLatency.TotalMilliseconds;
        while (probeLatencyMs <= 400 && latencyProbeClock.ElapsedMilliseconds < 300)
        {
            await Task.Delay(10);
            probeLatencyMs = playback.OutputLatency.TotalMilliseconds;
        }

        output.WriteLine($"上行探针延迟    : {probeLatencyMs:F1} ms（探针窗 {latencyProbeClock.ElapsedMilliseconds}ms）");
        Assert.True(
            probeLatencyMs > 400,
            $"回灌后输出延迟探针 {probeLatencyMs:F1}ms 未过 400ms：占用冲高没被 OutputLatency 一步跟上");

        // 等重同步点火（回灌后约 500ms）加余量走完，再采 3 秒中位看终态。
        await Task.Delay(1_500);
        var settledRingMs = await SampleRingMsAsync(renderer, seconds: 3);
        var latencyMs = playback.OutputLatency.TotalMilliseconds;
        var settled = renderer.ReadStats();
        var playedFinal = played.Snapshot();

        output.WriteLine($"终态占用中位    : {settledRingMs:F1} ms（target {targetMs}ms）");
        output.WriteLine($"终态输出延迟    : {latencyMs:F1} ms");
        output.WriteLine($"欠载 / 硬重置   : {settled.UnderrunCount} / {settled.HardResetCount}");

        // 顺序即纪律：先振幅复扫（回灌后声音还在出），再机制与数值断言。
        // 「还在出声」看 NonZeroCount 跨开闸增长：垫零块也触发 FramePlayed（内容全零），
        // 总数增长在闸下也成立，是弱形。
        Assert.True(
            playedFinal.NonZeroCount > atBackfill.NonZeroCount,
            "回灌后非零帧计数没有增长：开闸后没再出声");
        Assert.True(
            playedFinal.Peak >= AudioAlignmentThresholds.PeakAmplitudeLowerBound,
            $"已播出峰值 {playedFinal.Peak} 低于下界 {AudioAlignmentThresholds.PeakAmplitudeLowerBound}，注入疑似静音");
        Assert.True(
            NonZeroRatio(playedFinal) >= AudioAlignmentThresholds.NonZeroFrameRatioLowerBound,
            $"已播出非零帧占比 {NonZeroRatio(playedFinal):F2} 低于下界");

        // 垫零真实发生。
        Assert.True(settled.UnderrunCount > 0, "闸 500ms 后欠载计数仍为零：垫零没有发生");

        // 亚硬重置域的判别锚：欠载累计约 300ms 低于 500ms 阈，硬重置恒零——积压的
        // 剪除全靠重同步门，与长闸判据的恰一次硬重置判然两分。
        Assert.Equal(0, settled.HardResetCount);

        // 核心：重同步已剪积压，占用收干回 target 带。第 10 期「维持 430–570」的
        // 老锚在重同步门下确定性红，随第 11 期设计废止，重锚于此。
        Assert.InRange(settledRingMs, 150, 300);

        // 下行跟随（第 10 期交付的另一半）：积压剪掉后 OutputLatency 一步回落。
        Assert.True(
            Math.Abs(latencyMs - settledRingMs) <= followToleranceMs,
            $"|OutputLatency {latencyMs:F1} − 占用中位 {settledRingMs:F1}| 超 {followToleranceMs}ms：输出延迟没跟上重同步回落");

        // FIFO 臂在场证：offset 从未可用、误差从未在算，tracker 与重同步超额
        // 走的确是深度臂而非误差臂。
        Assert.False(settled.ClockOffsetAvailable, "对齐从未开启，offset 不该被判可用");
        Assert.Equal(0, settled.PlayTimeErrorUs);

        await sine.StopAsync(CancellationToken.None);
        playback.Configure(enabled: false, targetBufferMs: targetMs);
    }

    /// <summary>
    /// 长闸重同步的真机门控。沿 NetworkStallBackfill 的闸-回灌基建，但闸拉长到 3.5s，
    /// 穿过两个既有机制、落进重同步门的域：闸期 ring 先耗掉约 target 的真实帧再垫零，
    /// 欠载累计约 3.3s 越过 500ms 阈触发恰一次硬重置（连续欠载只重置一次，第 9 期
    /// 真机先例），重置后仍无帧，prefill 重攒态挂起；开闸整批回灌 3.5s 积压，ring
    /// 容量 2s 封顶（溢出丢最旧约 1.5s），占用瞬时约 2000ms，prefill（200ms）即满、
    /// 开闸出声；此后 FIFO 超额 = 2000 − 200 = 1800ms 大于 250ms 阈，持续 500ms
    /// （24_000 消费帧）重同步点火，丢最旧收干到 target 带，OutputLatency 跟随回落。
    ///
    /// 修前（无重同步门）该场景占用钉在约 2000ms 永不回落——FIFO 无控制律拉回，
    /// 硬重置只由欠载触发而积压恰恰不欠载——核心断言必红。
    /// </summary>
    [RealAudioFact]
    public async Task LongStallBackfill_ResyncTrimsOccupancyToTargetBand()
    {
        AudioRenderNative.ResetForTesting();

        using var renderer = new WasapiRenderer();
        Assert.True(renderer.IsAvailable, $"播放不可用：{renderer.FailureReason}");

        var played = new PlayedPcmRecorder();
        using var playback = new AudioPlaybackService(played, renderer);

        // 对齐不开启（FIFO，重同步门的深度臂），target 200ms，同 NetworkStallBackfill 开场。
        const int targetMs = 200;

        // 闸时长。垫零累计约 3500 − 200 = 3300ms，远越 500ms 硬重置阈（恰一次硬重置）；
        // 回灌积压 3500ms 超 ring 容量 2000ms（溢出封顶）——两个机制都构造性触发。
        const int stallMs = 3_500;

        // 跟随容差：死区 25 + 采集块 10 + 节流陈旧余量，同 NetworkStallBackfill。
        // 吞不掉 1800ms 的病灶量级。
        const double followToleranceMs = 50;

        playback.Configure(enabled: true, targetBufferMs: targetMs);
        Assert.True(playback.IsPlaying, $"起播失败：{playback.LastError}");

        // 喂帧闸，同 NetworkStallBackfill：常态直送，闸下入队缓存，开闸先按序整批
        // 回灌再恢复直送。Submit 收在锁内，回灌期间泵线程在锁上等。
        var gate = new object();
        var gateClosed = false;
        var held = new List<AudioFrame>();
        var stamper = new SenderTimelineStamper();
        using var sine = new SineFrameSource(ToneHz, Amplitude);
        sine.FrameAvailable += frame =>
        {
            lock (gate)
            {
                var stamped = stamper.Stamp(frame);
                if (gateClosed)
                {
                    held.Add(stamped);
                    return;
                }

                playback.Submit(stamped);
            }
        };
        await sine.StartAsync(CancellationToken.None);

        // 预填充与起播过渡走完再看。
        await Task.Delay(3_000);

        // 振幅地基先于一切时刻断言，纪律同上三条。
        var earlyPlayed = played.Snapshot();
        Assert.True(earlyPlayed.Count > 0, "FramePlayed 一次都没触发");
        Assert.True(
            earlyPlayed.Peak >= AudioAlignmentThresholds.PeakAmplitudeLowerBound,
            $"已播出峰值 {earlyPlayed.Peak} 低于下界 {AudioAlignmentThresholds.PeakAmplitudeLowerBound}，注入疑似静音");
        Assert.True(
            NonZeroRatio(earlyPlayed) >= AudioAlignmentThresholds.NonZeroFrameRatioLowerBound,
            $"已播出非零帧占比 {NonZeroRatio(earlyPlayed):F2} 低于下界");

        Assert.True(renderer.ReadStats().HasStarted, "统计说从未起播");

        // 基线：FIFO 稳态占用贴 target，OutputLatency 在跟随容差内。
        var baselineRingMs = await SampleRingMsAsync(renderer, seconds: 3);
        var baselineLatencyMs = playback.OutputLatency.TotalMilliseconds;
        output.WriteLine($"基线占用中位    : {baselineRingMs:F1} ms（target {targetMs}ms）");
        output.WriteLine($"基线输出延迟    : {baselineLatencyMs:F1} ms");
        Assert.InRange(baselineRingMs, targetMs - 50, targetMs + 50);
        Assert.True(
            Math.Abs(baselineLatencyMs - baselineRingMs) <= followToleranceMs,
            $"基线 |OutputLatency {baselineLatencyMs:F1} − 占用中位 {baselineRingMs:F1}| 超 {followToleranceMs}ms");

        // 闸 3.5s：耗掉约 target 的真实帧后垫零，欠载累计越过 500ms 阈硬重置恰一次，
        // 此后 prefill 重攒态挂起等帧。
        lock (gate)
        {
            gateClosed = true;
        }

        await Task.Delay(stallMs);

        // 开闸整批回灌 3.5s 积压：ring 容量 2s 封顶，占用瞬时约 2000ms。
        lock (gate)
        {
            foreach (var frame in held)
            {
                playback.Submit(frame);
            }

            held.Clear();
            gateClosed = false;
        }

        var atBackfill = played.Snapshot();

        // 立即探针：积压真的进了 ring。没有这一条，终态的「占用回 target 带」与
        // 「积压根本没进来」不可区分——修与不修都该绿的前提要单独钉住。
        // RingMs 是渲染轮快照（每轮 set_ring_frames），回灌完成到下一轮之间隔着
        // 一个消费节拍，零延迟单次读会撞上回灌前的旧快照——故短轮询。预算 300ms
        // 远小于 500ms 点火持续期，读到高水位时重同步必未点火，语义仍是「点火前
        // 进了 ring」；积压真没进来时轮询同样读不到，判别力不丢。
        var probeClock = Stopwatch.StartNew();
        var backfillProbeMs = renderer.ReadStats().RingMs;
        while (backfillProbeMs <= 1_000 && probeClock.ElapsedMilliseconds < 300)
        {
            await Task.Delay(10);
            backfillProbeMs = renderer.ReadStats().RingMs;
        }

        output.WriteLine($"回灌探针占用    : {backfillProbeMs:F1} ms（闸 {stallMs}ms，探针窗 {probeClock.ElapsedMilliseconds}ms）");
        Assert.True(
            backfillProbeMs > 1_000,
            $"回灌后占用探针 {backfillProbeMs:F1}ms 未过 1000ms：积压没进 ring，终态断言失去前提");

        // 等重同步点火（持续超额 500ms）加余量走完，再采 3 秒中位看终态。
        await Task.Delay(2_000);
        var settledRingMs = await SampleRingMsAsync(renderer, seconds: 3);
        var latencyMs = playback.OutputLatency.TotalMilliseconds;
        var settled = renderer.ReadStats();
        var playedFinal = played.Snapshot();

        output.WriteLine($"终态占用中位    : {settledRingMs:F1} ms（target {targetMs}ms）");
        output.WriteLine($"终态输出延迟    : {latencyMs:F1} ms");
        output.WriteLine($"欠载 / 硬重置   : {settled.UnderrunCount} / {settled.HardResetCount}");

        // 顺序即纪律：先振幅复扫（回灌后真声在出，NonZeroCount 跨开闸增长——垫零块
        // 也触发 FramePlayed，Count 增长是弱形），再机制与数值断言。
        Assert.True(
            playedFinal.NonZeroCount > atBackfill.NonZeroCount,
            "回灌后非零帧计数没有增长：开闸后没再出声");
        Assert.True(
            playedFinal.Peak >= AudioAlignmentThresholds.PeakAmplitudeLowerBound,
            $"已播出峰值 {playedFinal.Peak} 低于下界 {AudioAlignmentThresholds.PeakAmplitudeLowerBound}，注入疑似静音");
        Assert.True(
            NonZeroRatio(playedFinal) >= AudioAlignmentThresholds.NonZeroFrameRatioLowerBound,
            $"已播出非零帧占比 {NonZeroRatio(playedFinal):F2} 低于下界");

        // 垫零真实发生过。
        Assert.True(settled.UnderrunCount > 0, "闸 3.5s 后欠载计数仍为零：垫零没有发生");

        // 断流期恰一次硬重置（连续欠载只重置一次）；重同步不借道硬重置，回灌后不再加。
        Assert.Equal(1, settled.HardResetCount);

        // 核心：重同步已剪积压，占用收干回 target 带。修前（无重同步门）钉在约
        // 2000ms 永不回落，此断言必红。
        Assert.InRange(settledRingMs, 150, 300);

        // OutputLatency 照常跟随（第 10 期交付）：积压剪掉后一步回落。
        Assert.True(
            Math.Abs(latencyMs - settledRingMs) <= followToleranceMs,
            $"|OutputLatency {latencyMs:F1} − 占用中位 {settledRingMs:F1}| 超 {followToleranceMs}ms：输出延迟没跟上重同步回落");

        // FIFO 臂在场证：offset 从未可用、误差从未在算，重同步超额走的确是深度臂。
        Assert.False(settled.ClockOffsetAvailable, "对齐从未开启，offset 不该被判可用");
        Assert.Equal(0, settled.PlayTimeErrorUs);

        await sine.StopAsync(CancellationToken.None);
        playback.Configure(enabled: false, targetBufferMs: targetMs);
    }

    /// <summary>
    /// 声明预算 D 运行时下调的真机门控：执行器当场把占用剪到新 target 带。
    ///
    /// 必须跑在对齐臂上。执行器的点火闸在 offset 可用性上（启用位与 offset 非零
    /// 两者的合），FIFO 臂上 offset 恒不可用，拨 D 一帧都不会动 —— 那不是缺陷而是
    /// D 的有效域：没有跨机 offset 就无法把发送端时刻映射到本机轴，让目标深度去追
    /// D 是在追一个没有物理所指的数。故本判据带一句正面的 ClockOffsetAvailable
    /// 断言：闸关着的时候要红在闸上，不要红成「占用没动」而把人引到执行器去查。
    ///
    /// 阶跃前后误差都约零不是巧合，是前馈的定义 —— target 按 ΔD 走满 1:1、占用随之
    /// 跟到新 target，两者相消，故误差不变。这一条同时保证门不点火（超额阈 250ms）
    /// 与外环在判据窗内走不动多少（量级见 LinkTailEstimateMs 的注释），于是占用的
    /// 变化只能是执行器做的。
    ///
    /// 起播深度取 500ms 而不是别处那个 200ms：下调 300ms 之后落在 200ms，两端都
    /// 远离 native 的 [50, 1000] 夹取区间。若从 200ms 起播再下调 300ms，生效量会被
    /// 夹成 −150（目标深度落到下界），判据测的就不再是「剪到新 target」而是夹取行为。
    /// </summary>
    [RealAudioFact]
    public async Task DeclaredBudgetStepDown_ExecutorTrimsOccupancyToNewTargetBand()
    {
        AudioRenderNative.ResetForTesting();

        using var renderer = new WasapiRenderer();
        Assert.True(renderer.IsAvailable, $"播放不可用：{renderer.FailureReason}");

        var played = new PlayedPcmRecorder();
        using var playback = new AudioPlaybackService(played, renderer);

        const int targetMs = 500;
        const int stepMs = 300;
        const int newTargetMs = targetMs - stepMs;

        // 跟随容差。对齐臂上 OutputLatency 取的是 D 加误差，等价于占用加设备 padding
        // 加设备尾段（手动偏移为 0），故它与占用中位的差就是那两段之和，再叠一层死区
        // 25ms 即结构性上界。成分账：死区 25 + 本机 padding 约 11 + 本机尾段 10 ≈ 46，
        // 对 50 的纸面余量只有 4ms —— 而那 4ms 挂在本机端点属性上，不是结构常数：
        // DeviceLatencyUs 报得大的端点（蓝牙一档）会把上界顶过 50。本机实测这个差是
        // 12 到 22ms，当前形态是绿的；它要挡的病灶量级是 300ms，离 50 有六倍。
        const double followToleranceMs = 50;

        playback.ConfigureAlignment(
            enabled: true,
            dTicks: (targetMs + LinkTailEstimateMs) * TicksPerMs,
            offsetTicks: 1,
            manualOffsetTicks: 0);
        playback.Configure(enabled: true, targetBufferMs: targetMs);
        Assert.True(playback.IsPlaying, $"起播失败：{playback.LastError}");

        var stamper = new SenderTimelineStamper();
        using var sine = new SineFrameSource(ToneHz, Amplitude);
        sine.FrameAvailable += frame => playback.Submit(stamper.Stamp(frame));
        await sine.StartAsync(CancellationToken.None);

        // 预填充要攒满 500ms 才开闸，比 200ms 那几条判据长，等待期跟着加。
        await Task.Delay(4_000);

        // 振幅地基先于一切时刻断言，纪律同上四条。
        var earlyPlayed = played.Snapshot();
        Assert.True(earlyPlayed.Count > 0, "FramePlayed 一次都没触发");
        Assert.True(
            earlyPlayed.Peak >= AudioAlignmentThresholds.PeakAmplitudeLowerBound,
            $"已播出峰值 {earlyPlayed.Peak} 低于下界 {AudioAlignmentThresholds.PeakAmplitudeLowerBound}，注入疑似静音");
        Assert.True(
            NonZeroRatio(earlyPlayed) >= AudioAlignmentThresholds.NonZeroFrameRatioLowerBound,
            $"已播出非零帧占比 {NonZeroRatio(earlyPlayed):F2} 低于下界");

        var baseline = renderer.ReadStats();
        Assert.True(baseline.HasStarted, "统计说从未起播");
        Assert.True(
            baseline.DeviceClockAvailable,
            "端点无 IAudioClock：位置锚点不产生，对齐在本端点无从进行");
        Assert.True(
            baseline.ClockOffsetAvailable,
            "offset 未被判可用：执行器的点火闸关着，拨 D 一帧都不会动");

        var baselineRingMs = await SampleRingMsAsync(renderer, seconds: 3);
        var baselineErrorMs = MedianMs(await SampleErrorUsAsync(renderer, seconds: 2));
        output.WriteLine($"基线占用中位    : {baselineRingMs:F1} ms（target {targetMs}ms）");
        output.WriteLine($"基线误差中位    : {baselineErrorMs:F1} ms（D 拨在 {targetMs + LinkTailEstimateMs}ms 上）");
        Assert.InRange(baselineRingMs, targetMs - 50, targetMs + 50);
        // 误差远离 250ms 超额阈：门不点火，阶跃后占用的变化只能是执行器做的。
        Assert.InRange(baselineErrorMs, -100, 100);
        Assert.InRange(baseline.TargetMsCurrent, targetMs - 5, targetMs + 5);

        var atStep = played.Snapshot();

        // 阶跃：D 降 300ms。执行器当场把目标深度推到新值并真剪 300ms 占用。
        playback.ConfigureAlignment(
            enabled: true,
            dTicks: (newTargetMs + LinkTailEstimateMs) * TicksPerMs,
            offsetTicks: 1,
            manualOffsetTicks: 0);
        await Task.Delay(1_500);

        var settledRingMs = await SampleRingMsAsync(renderer, seconds: 3);
        var settledErrorMs = MedianMs(await SampleErrorUsAsync(renderer, seconds: 2));
        var latencyMs = playback.OutputLatency.TotalMilliseconds;
        var settled = renderer.ReadStats();
        var playedFinal = played.Snapshot();

        output.WriteLine($"阶跃后目标深度  : {settled.TargetMsCurrent} ms（期望 {newTargetMs}ms）");
        output.WriteLine($"阶跃后占用中位  : {settledRingMs:F1} ms");
        output.WriteLine($"阶跃后输出延迟  : {latencyMs:F1} ms");
        output.WriteLine($"阶跃后误差中位  : {settledErrorMs:F1} ms（前馈 1:1，故应仍约零）");
        output.WriteLine($"欠载 / 硬重置   : {settled.UnderrunCount} / {settled.HardResetCount}");

        // 顺序即纪律：先振幅复扫（剪掉 300ms 之后声音还在出），再机制与数值断言。
        Assert.True(
            playedFinal.NonZeroCount > atStep.NonZeroCount,
            "阶跃后非零帧计数没有增长：真剪之后没再出声");
        Assert.True(
            playedFinal.Peak >= AudioAlignmentThresholds.PeakAmplitudeLowerBound,
            $"已播出峰值 {playedFinal.Peak} 低于下界，阶跃后疑似静音");
        Assert.True(
            NonZeroRatio(playedFinal) >= AudioAlignmentThresholds.NonZeroFrameRatioLowerBound,
            $"已播出非零帧占比 {NonZeroRatio(playedFinal):F2} 低于下界");

        Assert.True(
            settled.ClockOffsetAvailable,
            "offset 中途失可用：执行器闸上了，把占用变化归因给阶跃不成立");

        // 前馈 1:1：目标深度按整笔 ΔD 走满。±5 的余量给外环 —— 阶跃后误差仍约零，外环
        // 的走步可忽略；只有一个设备缓冲时长的暂态里误差是 +300ms（D 已经降了，而正在
        // 出声的那个采样还是按旧深度排到端点的，误差为正即出声偏晚），限速 0.5ms/s 下
        // 往下界那侧走不到 0.02ms。
        Assert.InRange(settled.TargetMsCurrent, newTargetMs - 5, newTargetMs + 5);

        // 核心：占用真被剪到新 target 带。真剪过的那道上界钳按新目标深度托底，
        // 故落点就是新目标深度；±50 的带给推送块（20ms）与一轮消费量（约 11ms）
        // 叠出来的锯齿，以及采样窗内的自然游走。
        Assert.InRange(settledRingMs, newTargetMs - 50, newTargetMs + 50);

        // 真剪不借道硬重置：读游标前跳不清 ring、不重攒 prefill。
        Assert.Equal(0, settled.HardResetCount);

        Assert.True(
            Math.Abs(latencyMs - settledRingMs) <= followToleranceMs,
            $"|OutputLatency {latencyMs:F1} − 占用中位 {settledRingMs:F1}| 超 {followToleranceMs}ms：输出延迟没跟上执行器的真剪");

        await sine.StopAsync(CancellationToken.None);
        playback.Configure(enabled: false, targetBufferMs: targetMs);
    }

    /// <summary>
    /// 声明预算 D 运行时上调的真机门控：执行器跨轮垫零把占用垫到新 target 带，
    /// 而垫零帧不计欠载，故硬重置恒零。
    ///
    /// 本条兜两个无常驻单测的面。一是垫零消费路径：上调不当场做得完，读游标暂停、
    /// 某几轮不从缓冲读而直接写零，生产者继续写故占用上涨，跨若干轮摊完。二是欠载
    /// 隔离：垫零帧既不计 stats 的欠载数，也不计 prefill 的连续欠载。后者由
    /// HardResetCount 恒零来判 —— 垫零若被误计，连续欠载累计过 native 的
    /// 500ms 阈就触发硬重置，ring 全清、重新预填充，恰好毁掉执行器刚做到位的事，
    /// 而那条断言当场红。
    ///
    /// 上调量取 700ms 而不是 300ms：判别力要求垫零总量越过那个 500ms 阈。300ms 的
    /// 垫零即便被整笔误计成欠载也攒不到阈值，硬重置不会发生，那条断言在「隔离生效」
    /// 与「隔离撤回」两态下同为绿，判别力恰好为零。700ms 留 40% 的余量。两条上界
    /// 也核过：200 + 700 = 900 在 native 目标深度上界之内，占用峰值 900ms 在环形
    /// 缓冲容量（目标深度上界的两倍）之内，故垫零欠账不撞容量钳、R18 那条不对称
    /// 不在本判据的域里。
    ///
    /// 欠载计数另设一条判别：若垫零帧计进 stats 的欠载数，增量等于 500ms 除以一轮
    /// 时长（攒到 500ms 那一刻已硬重置），而一轮时长是端点属性 —— 本机一轮约 9.8ms，
    /// 实测得 51。要让这个增量跌破判别线 10，一轮得有 50ms 以上，共享模式给不出
    /// 那样的周期。本机稳态的真实欠载实测为零，10 的余量留给调度抖动 —— 判别的是
    /// 量级不是零，与「欠载只记录不设零阈」的先例不冲突。
    ///
    /// 同样必须跑在对齐臂上，正面的 ClockOffsetAvailable 断言理由同上一条判据。
    /// </summary>
    [RealAudioFact]
    public async Task DeclaredBudgetStepUp_PadsOccupancyToNewTargetBandWithoutHardReset()
    {
        AudioRenderNative.ResetForTesting();

        using var renderer = new WasapiRenderer();
        Assert.True(renderer.IsAvailable, $"播放不可用：{renderer.FailureReason}");

        var played = new PlayedPcmRecorder();
        using var playback = new AudioPlaybackService(played, renderer);

        const int targetMs = 200;
        const int stepMs = 700;
        const int newTargetMs = targetMs + stepMs;

        // 跟随容差与判据一同一个数、同一个理由，含那笔只剩 4ms 纸面余量的成分账。
        const double followToleranceMs = 50;

        // 欠载增量的判别线。取这个数的理由、以及垫零被误计时那个增量的量级，
        // 都写在本方法的 doc 里。
        const long jitterUnderrunAllowance = 10;

        playback.ConfigureAlignment(
            enabled: true,
            dTicks: (targetMs + LinkTailEstimateMs) * TicksPerMs,
            offsetTicks: 1,
            manualOffsetTicks: 0);
        playback.Configure(enabled: true, targetBufferMs: targetMs);
        Assert.True(playback.IsPlaying, $"起播失败：{playback.LastError}");

        var stamper = new SenderTimelineStamper();
        using var sine = new SineFrameSource(ToneHz, Amplitude);
        sine.FrameAvailable += frame => playback.Submit(stamper.Stamp(frame));
        await sine.StartAsync(CancellationToken.None);

        // 预填充与起播过渡走完再看。
        await Task.Delay(3_000);

        // 振幅地基先于一切时刻断言，纪律同上。
        var earlyPlayed = played.Snapshot();
        Assert.True(earlyPlayed.Count > 0, "FramePlayed 一次都没触发");
        Assert.True(
            earlyPlayed.Peak >= AudioAlignmentThresholds.PeakAmplitudeLowerBound,
            $"已播出峰值 {earlyPlayed.Peak} 低于下界 {AudioAlignmentThresholds.PeakAmplitudeLowerBound}，注入疑似静音");
        Assert.True(
            NonZeroRatio(earlyPlayed) >= AudioAlignmentThresholds.NonZeroFrameRatioLowerBound,
            $"已播出非零帧占比 {NonZeroRatio(earlyPlayed):F2} 低于下界");

        var baseline = renderer.ReadStats();
        Assert.True(baseline.HasStarted, "统计说从未起播");
        Assert.True(
            baseline.DeviceClockAvailable,
            "端点无 IAudioClock：位置锚点不产生，对齐在本端点无从进行");
        Assert.True(
            baseline.ClockOffsetAvailable,
            "offset 未被判可用：执行器的点火闸关着，拨 D 一帧都不会动");

        var baselineRingMs = await SampleRingMsAsync(renderer, seconds: 3);
        var baselineErrorMs = MedianMs(await SampleErrorUsAsync(renderer, seconds: 2));
        output.WriteLine($"基线占用中位    : {baselineRingMs:F1} ms（target {targetMs}ms）");
        output.WriteLine($"基线误差中位    : {baselineErrorMs:F1} ms（D 拨在 {targetMs + LinkTailEstimateMs}ms 上）");
        output.WriteLine($"基线欠载 / 重置 : {baseline.UnderrunCount} / {baseline.HardResetCount}");
        Assert.InRange(baselineRingMs, targetMs - 50, targetMs + 50);
        Assert.InRange(baselineErrorMs, -100, 100);
        Assert.InRange(baseline.TargetMsCurrent, targetMs - 5, targetMs + 5);

        var atStep = played.Snapshot();

        // 阶跃：D 升 700ms。执行器把目标深度推到新值并欠下 700ms 的垫零，跨约 63 轮
        // 摊完；垫零轮不从缓冲读，生产者继续写，故占用一路涨到新目标深度。
        playback.ConfigureAlignment(
            enabled: true,
            dTicks: (newTargetMs + LinkTailEstimateMs) * TicksPerMs,
            offsetTicks: 1,
            manualOffsetTicks: 0);
        await Task.Delay(1_500);

        var settledRingMs = await SampleRingMsAsync(renderer, seconds: 3);
        var settledErrorMs = MedianMs(await SampleErrorUsAsync(renderer, seconds: 2));
        var latencyMs = playback.OutputLatency.TotalMilliseconds;
        var settled = renderer.ReadStats();
        var playedFinal = played.Snapshot();

        output.WriteLine($"阶跃后目标深度  : {settled.TargetMsCurrent} ms（期望 {newTargetMs}ms）");
        output.WriteLine($"阶跃后占用中位  : {settledRingMs:F1} ms");
        output.WriteLine($"阶跃后输出延迟  : {latencyMs:F1} ms");
        output.WriteLine($"阶跃后误差中位  : {settledErrorMs:F1} ms（前馈 1:1，故应仍约零）");
        output.WriteLine($"欠载 / 硬重置   : {settled.UnderrunCount} / {settled.HardResetCount}（垫零 {stepMs}ms 跨约 63 轮）");

        // 顺序即纪律：先振幅复扫。看非零帧计数跨阶跃增长而不是总数 —— 垫零块也触发
        // FramePlayed（内容全零），总数增长在垫零期间同样成立，是弱形。
        Assert.True(
            playedFinal.NonZeroCount > atStep.NonZeroCount,
            "阶跃后非零帧计数没有增长：垫零摊完之后没再出声");
        Assert.True(
            playedFinal.Peak >= AudioAlignmentThresholds.PeakAmplitudeLowerBound,
            $"已播出峰值 {playedFinal.Peak} 低于下界，阶跃后疑似静音");
        Assert.True(
            NonZeroRatio(playedFinal) >= AudioAlignmentThresholds.NonZeroFrameRatioLowerBound,
            $"已播出非零帧占比 {NonZeroRatio(playedFinal):F2} 低于下界");

        Assert.True(
            settled.ClockOffsetAvailable,
            "offset 中途失可用：执行器闸上了，把占用变化归因给阶跃不成立");

        // 前馈 1:1，±5 的余量同判据一。
        Assert.InRange(settled.TargetMsCurrent, newTargetMs - 5, newTargetMs + 5);

        // 核心之一：占用真被垫到新 target 带。垫零期间不消费而生产继续，故涨幅恰是
        // 垫零量；±50 的带同判据一。
        Assert.InRange(settledRingMs, newTargetMs - 50, newTargetMs + 50);

        // 核心之二（本期权重最高的一条断言）：垫零帧不计 prefill 的连续欠载。
        // 计进去的话 700ms 的垫零攒过 500ms 阈即硬重置，ring 全清、重新预填充。
        Assert.Equal(0, settled.HardResetCount);

        // 同一条隔离的另一半：垫零帧也不计 stats 的欠载数。硬重置那条断言只看得见
        // 累计过阈的后果，这条直接看计数本身。
        Assert.True(
            settled.UnderrunCount - baseline.UnderrunCount <= jitterUnderrunAllowance,
            $"欠载计数跨阶跃增了 {settled.UnderrunCount - baseline.UnderrunCount}（允许 {jitterUnderrunAllowance}）：垫零帧疑似被计进欠载");

        Assert.True(
            Math.Abs(latencyMs - settledRingMs) <= followToleranceMs,
            $"|OutputLatency {latencyMs:F1} − 占用中位 {settledRingMs:F1}| 超 {followToleranceMs}ms：输出延迟没跟上跨轮垫零");

        await sine.StopAsync(CancellationToken.None);
        playback.Configure(enabled: false, targetBufferMs: targetMs);
    }

    /// <summary>
    /// 重同步门点火而丢帧量被钳成零时，外环残差不被清掉的真机门控。
    ///
    /// 构造的域：出声时刻误差持续约 400ms（超额过 250ms 阈，门每 500ms 点火一次），
    /// 而占用低于目标深度（盈余为负，那道上界钳把丢帧量钳成 0）。点火块此前
    /// 无条件清外环残差，而残差要攒够 1ms 才进位、限速 0.5ms/s 下要两秒，每 500ms
    /// 被清一次就永远进不了位 —— TargetMsCurrent 恒定不动。改成真剪了才清之后，
    /// 残差照攒，目标深度单调下行。
    ///
    /// 前两条 D 阶跃判据走不到这个域，故本条不可被它们替代：执行器在阶跃那一刻就把
    /// 位移做掉了，前馈 1:1 使误差不变，门根本不点火。
    ///
    /// 误差用手动偏移造：它加在实际出声那一侧（补自动估计没看见的那段硬件延迟），
    /// 加 400ms 即「实际出声比目标晚 400ms」，符号与门要的正超额一致。取 400 而不是
    /// 刚过阈的 260：外环速率上限在 400ms 误差下咬合（400 除以三倍内环时间常数得
    /// 0.67ms/s，大于上限 0.5ms/s），于是下行速率由上限一处定，与误差的具体值、
    /// 与目标深度都无关，等待时长的推导只依赖那一个常量。20 秒的窗按它推出约 10ms
    /// 的下行，判据线取 2ms。
    ///
    /// 占用低于目标深度这一半必须自己造，不能靠「稳态占用天然略低于目标深度」：
    /// 内环是比例控制，稳态的占用减目标深度约等于负的一千倍晶振相对偏差乘目标深度，
    /// 符号由本机设备时钟相对 QPC 的快慢决定，是这台机器的属性。符号翻过来时门每次
    /// 点火都真剪一小段锯齿峰，外环残差照样被清，判据就退化成与修前不可分辨的假红。
    ///
    /// 造法是把喂帧改成定深延迟线：先闸 120ms 只入队不喂（ring 被消费掉这么多），
    /// 此后进一出一，积压永不回灌。管道总延迟守恒 —— 延迟线加了 120ms、ring 减了
    /// 120ms —— 故出声时刻误差不变，超额仍只由手动偏移提供；闸前闸后各测一次误差
    /// 中位钉住这一条。占用落在 80ms 上，远高于零，闸期与窗内都不欠载。
    ///
    /// 门是否真点了火，RenderStats 十六字段里没有直接对应的字段 —— 钳成 0 时读游标
    /// 一帧都没动，那正是本域的定义。窗内逐点过阈与消费不停（已播出帧在长）是点火的
    /// 必要指示，不是充分条件，两处漏得掉：门的持续期在超额跌回阈下的任何一轮立即
    /// 清零，而 1Hz 的采样看不见亚秒级的回落；且 native 折毫秒是向零截断之后才判
    /// 大于 250，门实际要的是误差不小于 251ms，而断言只要求大于 250_000us。
    ///
    /// 充分的形态是「超额在每两秒里至少连续过阈 500ms」—— 两秒是残差按 0.5ms/s 攒够
    /// 一毫秒进位的周期，比它更疏的点火抹不掉进位。而点火确实在发生，由 F1 撤回刀
    /// 坐实：那把刀下目标深度的下行量恰为 0，只有门确实在每两秒内至少清掉一次残差才做得到。
    /// </summary>
    [RealAudioFact]
    public async Task SustainedExcessWithZeroSurplus_OuterLoopKeepsMovingTargetDepth()
    {
        AudioRenderNative.ResetForTesting();

        using var renderer = new WasapiRenderer();
        Assert.True(renderer.IsAvailable, $"播放不可用：{renderer.FailureReason}");

        var played = new PlayedPcmRecorder();
        using var playback = new AudioPlaybackService(played, renderer);

        const int targetMs = 200;

        // 闸时长即延迟线的定深，也就是占用低于目标深度的量。120ms 的选取：要盖住
        // 推送块与一轮消费量叠出的锯齿（约 30ms 峰峰）加上窗内的下行量（约 10ms），
        // 又要让占用留在 80ms 上不欠载 —— 第 11 期的对齐臂判据在 44 到 69ms 的占用上
        // 跑了六分半，欠载为零。
        //
        // 这个亏空不是永久的，它两侧一起收窄：内环按盈亏差回填（亏空 120ms 时是
        // 120/200 乘 MAX_DRIFT，约 0.6ms/s，随亏空变缓），外环下行再吃 0.5ms/s，
        // 起步约 1.1ms/s，归零要一百秒以上。20 秒的窗离它有五倍 —— 调大
        // observeSeconds 会静默走出「盈余为负」这个域，那时判据不再测 F1。
        const int holdMs = 120;
        const int manualOffsetMs = 400;

        // 观测窗。按 0.5ms/s 的速率上限推出约 10ms 的下行，判据线 2ms 留五倍余量。
        const int observeSeconds = 20;
        const int requiredDropMs = 2;

        var dTicks = (targetMs + LinkTailEstimateMs) * TicksPerMs;

        playback.ConfigureAlignment(
            enabled: true, dTicks: dTicks, offsetTicks: 1, manualOffsetTicks: 0);
        playback.Configure(enabled: true, targetBufferMs: targetMs);
        Assert.True(playback.IsPlaying, $"起播失败：{playback.LastError}");

        // 定深延迟线：常态进一出一；闸下只入队不出队。Submit 收在锁内，与既有两条
        // 闸判据同形，而这里根本没有回灌 —— 队列只是恒深。
        var gate = new object();
        var holding = false;
        var pending = new Queue<AudioFrame>();
        var stamper = new SenderTimelineStamper();
        using var sine = new SineFrameSource(ToneHz, Amplitude);
        sine.FrameAvailable += frame =>
        {
            lock (gate)
            {
                pending.Enqueue(stamper.Stamp(frame));
                if (holding)
                {
                    return;
                }

                playback.Submit(pending.Dequeue());
            }
        };
        await sine.StartAsync(CancellationToken.None);

        // 预填充与起播过渡走完再看。
        await Task.Delay(3_000);

        // 振幅地基先于一切时刻断言，纪律同上。
        var earlyPlayed = played.Snapshot();
        Assert.True(earlyPlayed.Count > 0, "FramePlayed 一次都没触发");
        Assert.True(
            earlyPlayed.Peak >= AudioAlignmentThresholds.PeakAmplitudeLowerBound,
            $"已播出峰值 {earlyPlayed.Peak} 低于下界 {AudioAlignmentThresholds.PeakAmplitudeLowerBound}，注入疑似静音");
        Assert.True(
            NonZeroRatio(earlyPlayed) >= AudioAlignmentThresholds.NonZeroFrameRatioLowerBound,
            $"已播出非零帧占比 {NonZeroRatio(earlyPlayed):F2} 低于下界");

        var opening = renderer.ReadStats();
        Assert.True(opening.HasStarted, "统计说从未起播");
        Assert.True(
            opening.DeviceClockAvailable,
            "端点无 IAudioClock：位置锚点不产生，对齐在本端点无从进行");
        Assert.True(
            opening.ClockOffsetAvailable,
            "offset 未被判可用：误差臂停摆，门的超额会退回深度臂，本判据的域不成立");

        var beforeHoldErrorMs = MedianMs(await SampleErrorUsAsync(renderer, seconds: 2));

        // 闸一次造出占用亏空，此后延迟线恒深。
        lock (gate)
        {
            holding = true;
        }

        await Task.Delay(holdMs);

        lock (gate)
        {
            holding = false;
        }

        // 亏空的落定只需一轮渲染，两秒是给锯齿与采样留的余量。
        await Task.Delay(2_000);

        var deficitRingMs = await SampleRingMsAsync(renderer, seconds: 3);
        var afterHoldErrorMs = MedianMs(await SampleErrorUsAsync(renderer, seconds: 2));
        var armed = renderer.ReadStats();
        output.WriteLine($"闸前误差中位    : {beforeHoldErrorMs:F1} ms（D 拨在 {targetMs + LinkTailEstimateMs}ms 上）");
        output.WriteLine($"闸后误差中位    : {afterHoldErrorMs:F1} ms（管道总延迟守恒，故应不变）");
        output.WriteLine($"闸后占用中位    : {deficitRingMs:F1} ms（target {armed.TargetMsCurrent}ms，闸 {holdMs}ms）");
        output.WriteLine($"闸后欠载 / 重置 : {armed.UnderrunCount} / {armed.HardResetCount}");

        // 域的前提之一：占用低于目标深度。50ms 的余量盖住锯齿峰。
        Assert.True(
            deficitRingMs < armed.TargetMsCurrent - 50,
            $"闸后占用中位 {deficitRingMs:F1}ms 未低于目标深度 {armed.TargetMsCurrent}ms 减 50：亏空没造出来，盈余不为负，门点火会真剪");

        // 延迟线不改误差：延迟线加的与 ring 减的是同一个量。这一条同时钉住「超额只由
        // 手动偏移提供」—— 闸本身若把误差推过阈，下面的归因就不成立。
        Assert.InRange(afterHoldErrorMs, beforeHoldErrorMs - 40, beforeHoldErrorMs + 40);
        Assert.InRange(afterHoldErrorMs, -100, 100);
        Assert.Equal(0, armed.HardResetCount);

        // 下发手动偏移：误差瞬间变为约 +400ms。D 一动不动，故执行器不点火（阶跃检测
        // 的锚只跟 D 走），此后目标深度的任何移动都只能是外环写的。
        playback.ConfigureAlignment(
            enabled: true,
            dTicks: dTicks,
            offsetTicks: 1,
            manualOffsetTicks: manualOffsetMs * TicksPerMs);
        await Task.Delay(1_000);

        var startTargetMs = renderer.ReadStats().TargetMsCurrent;
        var trajectory = new List<(double Seconds, long ErrorUs, int TargetMs, double RingMs)>();
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < observeSeconds)
        {
            await Task.Delay(1_000);
            var probe = renderer.ReadStats();
            trajectory.Add((
                clock.Elapsed.TotalSeconds, probe.PlayTimeErrorUs, probe.TargetMsCurrent, probe.RingMs));
        }

        foreach (var point in trajectory)
        {
            output.WriteLine(
                $"t+{point.Seconds,4:F0}s : e={point.ErrorUs,7}us target={point.TargetMs}ms ring={point.RingMs:F0}ms");
        }

        var settled = renderer.ReadStats();
        var playedFinal = played.Snapshot();
        var endTargetMs = trajectory[^1].TargetMs;
        output.WriteLine($"目标深度        : 起点 {startTargetMs}ms → 终点 {endTargetMs}ms（下行 {startTargetMs - endTargetMs}ms）");
        output.WriteLine($"欠载 / 硬重置   : {settled.UnderrunCount} / {settled.HardResetCount}");

        // 顺序即纪律：先振幅复扫，再机制与数值断言。
        Assert.True(
            playedFinal.NonZeroCount > earlyPlayed.NonZeroCount,
            "窗内非零帧计数没有增长：消费停了，而门的持续期按消费帧数计，也就不再推进");
        Assert.True(
            playedFinal.Peak >= AudioAlignmentThresholds.PeakAmplitudeLowerBound,
            $"已播出峰值 {playedFinal.Peak} 低于下界，疑似静音");
        Assert.True(
            NonZeroRatio(playedFinal) >= AudioAlignmentThresholds.NonZeroFrameRatioLowerBound,
            $"已播出非零帧占比 {NonZeroRatio(playedFinal):F2} 低于下界");

        Assert.True(
            settled.ClockOffsetAvailable,
            "offset 中途失可用：误差臂停摆，门的超额退回深度臂，本判据的域不成立");

        // 域的前提之一：超额逐点过阈。它是点火的必要指示而不是充分条件（1Hz 采样
        // 看不见亚秒级回落，且门折毫秒后要的是 ≥ 251ms），充分形态与点火的实测出处
        // 都在本方法的 doc 里。
        Assert.True(
            trajectory.All(point => point.ErrorUs > 250_000),
            $"窗内误差最小 {trajectory.Min(point => point.ErrorUs)}us 未过 250ms 超额阈：门不点火，本判据测不到那个域");

        // 域的前提：盈余恒负 → 丢帧量恒被钳成 0。占用取逐秒的瞬时样本而不是中位，
        // 锯齿的峰也要落在目标深度以下。
        Assert.True(
            trajectory.All(point => point.RingMs < point.TargetMs),
            "窗内出现占用不低于目标深度的样本：那一轮点火会真剪，外环残差被清，归因不成立");

        // 核心：外环残差没被每 500ms 抹一次，目标深度真的在动。误差为正只驱动下行，
        // 故逐点不上行；总量取判据线。
        for (var i = 1; i < trajectory.Count; i++)
        {
            Assert.True(
                trajectory[i].TargetMs <= trajectory[i - 1].TargetMs,
                $"目标深度在 t+{trajectory[i].Seconds:F0}s 上行到 {trajectory[i].TargetMs}ms（上一点 {trajectory[i - 1].TargetMs}ms）：误差为正时外环只该下行");
        }

        Assert.True(
            startTargetMs - endTargetMs >= requiredDropMs,
            $"目标深度只下行了 {startTargetMs - endTargetMs}ms（起点 {startTargetMs} → 终点 {endTargetMs}，判据线 {requiredDropMs}ms）：外环残差疑似每次点火都被清掉");

        // 门不借道硬重置，垫零也没发生过：本判据全程在亚欠载域里。
        Assert.Equal(0, settled.HardResetCount);

        await sine.StopAsync(CancellationToken.None);
        playback.Configure(enabled: false, targetBufferMs: targetMs);
    }

    private async Task<List<long>> SampleErrorUsAsync(WasapiRenderer renderer, int seconds)
    {
        var samples = new List<long>();
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            samples.Add(renderer.ReadStats().PlayTimeErrorUs);
            await Task.Delay(100);
        }

        return samples;
    }

    private async Task<double> SampleRingMsAsync(WasapiRenderer renderer, int seconds)
    {
        var samples = new List<double>();
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            // 折算收在 AudioRenderStats.RingMs 一处（按传输采样率），这里不再写换算式：
            // 同一个 48000 散成两份各自为真的声明，改一处漏一处不会让任何判据变红。
            samples.Add(renderer.ReadStats().RingMs);
            await Task.Delay(50);
        }

        samples.Sort();
        return samples[samples.Count / 2];
    }

    private static double MedianMs(List<long> errorsUs)
    {
        var sorted = errorsUs.OrderBy(e => e).ToList();
        return sorted[sorted.Count / 2] / 1_000.0;
    }

    private static double NonZeroRatio((int Count, int NonZeroCount, int Peak) snapshot) =>
        snapshot.Count == 0 ? 0 : (double)snapshot.NonZeroCount / snapshot.Count;

    /// <summary>
    /// 收已播出帧并扫振幅。只留计数不攒字节——第一条判据要跑六分钟以上，
    /// 攒 PCM 是几百 MB。
    /// </summary>
    private sealed class PlayedPcmRecorder : IAudioFrameSubmitter
    {
        private readonly object _gate = new();
        private int _count;
        private int _nonZeroCount;
        private int _peak;

        public void Submit(AudioFrame frame)
        {
            var (peak, any) = PcmScan.Scan(frame.Pcm);
            lock (_gate)
            {
                _count++;
                if (peak > _peak)
                {
                    _peak = peak;
                }

                if (any)
                {
                    _nonZeroCount++;
                }
            }
        }

        public (int Count, int NonZeroCount, int Peak) Snapshot()
        {
            lock (_gate)
            {
                return (_count, _nonZeroCount, _peak);
            }
        }
    }

    /// <summary>
    /// 内容线性的发送端时间戳：首块取单调钟为基准，此后按累计帧数推。
    /// 与真实发送端「采样时刻跟随内容」同形；注入泵的发块时刻带簇状锯齿，
    /// 直接用它当时间戳会给锚点注入十几毫秒的抖动。
    /// 只在泵线程上被调，不需要同步。
    /// </summary>
    private sealed class SenderTimelineStamper
    {
        private long _baseTicks;
        private long _framesStamped;

        public AudioFrame Stamp(AudioFrame frame)
        {
            if (_baseTicks == 0)
            {
                _baseTicks = MonotonicClock.Now100Ns();
            }

            var ticks = _baseTicks + _framesStamped * TicksPerMs * 1_000 / SineFrameSource.SampleRate;
            _framesStamped += frame.FrameCount;
            return frame with { SenderTimelineTicks100Ns = ticks };
        }
    }
}
