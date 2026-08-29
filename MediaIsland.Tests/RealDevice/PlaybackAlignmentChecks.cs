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
/// 跨机对齐的真机判据。三条门控：对齐精度、设备会话事实与垫零偏移被输出延迟跟随的实测。
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
    /// 校准后注入的初始误差，毫秒。取负方向（目标出声时刻晚于实际）：外环要抬高
    /// 目标深度去追，向上远离 MIN 夹紧边界；正方向在下限深度起播时立即撞夹紧，
    /// 外环没有行程。20ms 远大于死区（1ms）与预算（5ms），保证「外环停掉必红」
    /// 与「等待缩短必红」都是构造性的，不靠运气。
    /// </summary>
    private const double InjectedErrorMs = 20.0;

    /// <summary>
    /// 校准前的发送端延迟预算初值，毫秒。量级取「下限深度 + 设备尾段」的估计，
    /// 让校准期误差不至于大到把外环推上速率上限（那会在校准窗内拖着目标深度走）。
    /// 精确值无所谓——校准会把它拨到误差约零的位置。
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

        // 注入已知初始误差：D 拨到「校准零点 + 20ms」。改的是目标出声时刻，
        // 实际出声没动，于是误差瞬间变为约 -20ms，只有外环能把它修掉。
        var injectedDTicks = InitialDelayBudgetMs * TicksPerMs
            + (long)Math.Round((rawErrorMs + InjectedErrorMs) * TicksPerMs);
        playback.ConfigureAlignment(true, injectedDTicks, offsetTicks: 1, manualOffsetTicks: 0);
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
    /// 垫零偏移被 OutputLatency 跟随的真机门控。真实病灶是网络卡顿：发送端持续产帧，
    /// 帧被延迟后整批补达。接收端 ring 先耗尽 target 时长的真实帧、再垫零，积压到达后
    /// 占用回填到卡顿时长的水平并持续——占用大于 target 的恒偏就是歌词超前的根源，
    /// 本判据钉的是 OutputLatency 跟随这个恒偏而不是恒回起播常量。
    ///
    /// 注入必须闸住缓存、到点整批回灌，不能停源再重启：停掉的源不产生积压，
    /// 恢复后占用趋零，方向就反了，测不到病灶。
    ///
    /// 数值账：对齐关（FIFO，默认病灶路径），target 200ms。闸 500ms：ring 先耗掉约
    /// 200ms 真实帧，再垫约 300ms 零（UnderrunCount 大于 0）；欠载累计约 300ms 低于
    /// 500ms 硬重置阈（第 9 期判据：累积过 500ms 触发恰一次硬重置）→ HardResetCount
    /// 恒 0，钉住「偏移持续存在恰因未硬重置」的机制边界。回灌 500ms 积压 → 占用回填至
    /// 约 500ms 并稳态维持（FIFO 无控制律拉回）。OutputLatency（节流 100ms + 死区
    /// 25ms）应跟随到占用水平——修前它恒回 200，与约 500 的占用差 300ms。
    /// </summary>
    [RealAudioFact]
    public async Task NetworkStallBackfill_OutputLatencyFollowsOccupancyOffset()
    {
        AudioRenderNative.ResetForTesting();

        using var renderer = new WasapiRenderer();
        Assert.True(renderer.IsAvailable, $"播放不可用：{renderer.FailureReason}");

        var played = new PlayedPcmRecorder();
        using var playback = new AudioPlaybackService(played, renderer);

        // 对齐不开启（不调 ConfigureAlignment），比对 DeviceSessionFacts 的关闭路径开场。
        // 帧照带发送端时间戳——关闭路径承诺「时刻在场也不走时间轴」的强形。
        const int targetMs = 200;

        // 卡顿时长。垫零约 500 - 200 = 300ms，离 500ms 硬重置阈有 200ms 的构造性余量；
        // 回填约 500ms，与 target 差 300ms，远大于 50ms 跟随容差——两头都不靠运气。
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

        // 等节流窗翻过与占用稳定，再采 3 秒中位。
        await Task.Delay(1_000);
        var backfillRingMs = await SampleRingMsAsync(renderer, seconds: 3);
        var latencyMs = playback.OutputLatency.TotalMilliseconds;
        var stalled = renderer.ReadStats();
        var playedFinal = played.Snapshot();

        output.WriteLine($"闸后占用中位    : {backfillRingMs:F1} ms（闸 {stallMs}ms）");
        output.WriteLine($"闸后输出延迟    : {latencyMs:F1} ms");
        output.WriteLine($"欠载 / 硬重置   : {stalled.UnderrunCount} / {stalled.HardResetCount}");

        // 顺序即纪律：先振幅复扫（回灌后声音还在出），再机制与时刻断言。
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
        Assert.True(stalled.UnderrunCount > 0, "闸 500ms 后欠载计数仍为零：垫零没有发生");

        // 未借道硬重置：欠载累计约 300ms 低于 500ms 阈，偏移持续存在恰因如此。
        Assert.Equal(0, stalled.HardResetCount);

        // 回填到约「卡顿时长」的水平并稳态维持（FIFO 无控制律拉回）。
        Assert.InRange(backfillRingMs, 430, 570);

        // 核心：OutputLatency 跟随占用。修前出声臂恒回 target，与约 500 的占用差
        // 300ms，此断言必红；修后 FIFO 臂消费 RingMs，一步跟随后必绿。
        Assert.True(
            Math.Abs(latencyMs - backfillRingMs) <= followToleranceMs,
            $"|OutputLatency {latencyMs:F1} − 占用中位 {backfillRingMs:F1}| 超 {followToleranceMs}ms：输出延迟没跟上垫零偏移");

        // FIFO 臂选路的真机在场证：offset 从未可用、误差从未在算，
        // tracker 消费的确是占用而非误差。
        Assert.False(stalled.ClockOffsetAvailable, "对齐从未开启，offset 不该被判可用");
        Assert.Equal(0, stalled.PlayTimeErrorUs);

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
