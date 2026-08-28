using MediaIsland.Models;
using MediaIsland.Services.Audio;
using MediaIsland.Services.Audio.Playback;
using MediaIsland.Services.MediaLink;
using MediaIsland.Services.MediaLink.Protocol;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MediaIsland.Tests.MediaLink;

/// <summary>
/// 对齐协调方：判定的唯一生产调用点。这里钉四件事——设备事实真的进了判定、
/// 结论真的到了渲染器、退回不打断播放、归因只在转移时落一次日志。
/// 这一层断的话没有任何报错：判定永远没人调，或者调了结论没人听，声音都照出。
/// </summary>
public class MediaLinkAlignmentCoordinatorTests
{
    private sealed class NullSubmitter : IAudioFrameSubmitter
    {
        public void Submit(AudioFrame frame) { }
    }

    private sealed class RecordingRenderer : IAudioRenderer
    {
        public bool IsAvailable => true;
        public string? FailureReason => null;
        public event Action<AudioFrame>? FramePlayed { add { } remove { } }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }

        /// <summary>最近一次 Start 携带的对齐开关。null 表示还没起播过。</summary>
        public bool? LastStartAlignmentEnabled { get; private set; }

        public (long DTicks, long OffsetTicks, long ManualOffsetTicks)? LastAlignment
        {
            get;
            private set;
        }

        public AudioRenderStats Stats { get; set; }

        public void SetAlignment(long dTicks, long offsetTicks, long manualOffsetTicks) =>
            LastAlignment = (dTicks, offsetTicks, manualOffsetTicks);

        public void Start(int targetBufferMs, bool alignmentEnabled)
        {
            StartCount++;
            LastStartAlignmentEnabled = alignmentEnabled;
        }

        public void Stop() => StopCount++;

        public void Push(byte[] pcm, long senderTicks) { }

        public AudioRenderStats ReadStats() => Stats;

        public (int MinTargetMs, int MaxTargetMs) TargetDepthBounds() => (50, 1000);

        public void Dispose() { }
    }

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    /// <summary>本机事实：10ms 设备延迟 + 20ms 端点缓冲（48k 域 960 帧），已起播。</summary>
    private static AudioRenderStats StartedStats() => new(
        RingFrames: 0, UnderrunCount: 0, HardResetCount: 0, DeviceFramesRendered: 0,
        DeviceSampleRate: 48_000, ResampleRatioPpm: 0,
        DeviceLatencyUs: 10_000, DeviceBufferFrames: 960, DeviceClockAvailable: true);

    private static MediaLinkServerDeclaration Declared(long? budgetMs) => new(
        new HashSet<string>(StringComparer.Ordinal)
        {
            MediaLinkProtocol.CapabilityAudio, MediaLinkProtocol.CapabilityAudioClock
        },
        budgetMs);

    private sealed class Harness
    {
        public RecordingRenderer Renderer { get; } = new();
        public AudioPlaybackService Playback { get; }
        public MediaLinkAlignmentCoordinator Coordinator { get; }
        public ListLogger Logger { get; } = new();

        public MediaLinkServerDeclaration Declaration { get; set; } = Declared(300);
        public (bool Available, long OffsetTicks) WireOffset { get; set; } = (true, 4242);
        public bool Enabled { get; set; } = true;

        /// <summary>当前播放设备。可切换，模拟用户换默认输出设备。</summary>
        public string? DeviceId { get; set; } = "dev-a";

        /// <summary>
        /// 手动偏移走真实的 PluginSettings 而不是假委托：生产接线就是
        /// settings.GetManualOffsetMs，这里复用同一条组合，「未知设备取 0」
        /// 之类的规则才是被这条链实测过，而不是被测试自己抄了一遍。
        /// </summary>
        public PluginSettings Settings { get; } = new();

        public Harness()
        {
            Playback = new AudioPlaybackService(
                new NullSubmitter(), Renderer, deviceIdProvider: () => DeviceId);
            Coordinator = new MediaLinkAlignmentCoordinator(
                Playback,
                () => Declaration,
                () => WireOffset,
                () => Enabled,
                deviceId => Settings.GetManualOffsetMs(deviceId),
                Logger);
        }
    }

    [Fact]
    public void EverythingInPlace_PushesBudgetInTicksAndTheWireOffset()
    {
        var harness = new Harness();
        harness.Renderer.Stats = StartedStats();
        harness.Playback.Configure(enabled: true, targetBufferMs: 200);

        harness.Coordinator.Recompute();

        // D 按毫秒乘一万换算成 100ns；offset 原样透传。这一条断的是「设备事实进了判定、
        // 结论到了渲染器」整条链，而不是判定函数本身——那在 MediaLinkAlignmentPolicyTests。
        Assert.Equal((300L * 10_000, 4242L, 0L), harness.Renderer.LastAlignment);
    }

    [Fact]
    public void AnInfeasibleBudget_KeepsTheSwitchButZeroesTheTargets()
    {
        // 预算 70 减设备延迟 10 与缓冲 20 后剩 40，装不下最小缓冲 50。开关照存——
        // 它决定下一次起播那一刻建不建执行器；但 D 与 offset 必须归零让外环不动，
        // 否则外环拿着一个已被判死的目标继续走步，正是「假装对齐」的形态。
        var harness = new Harness { Declaration = Declared(70) };
        harness.Renderer.Stats = StartedStats();
        harness.Playback.Configure(enabled: true, targetBufferMs: 200);

        harness.Coordinator.Recompute();

        Assert.Equal((0L, 0L, 0L), harness.Renderer.LastAlignment);
    }

    [Fact]
    public void TheDeviceFacts_ActuallyReachTheDecision()
    {
        // 同一个预算，只有把缓冲容量算进下限才会判退回：预算 75、延迟 10、缓冲 20，
        // 余量 45 < 50。漏接缓冲（或漏接延迟）时这里会走成对齐。
        var harness = new Harness { Declaration = Declared(75) };
        harness.Renderer.Stats = StartedStats();
        harness.Playback.Configure(enabled: true, targetBufferMs: 200);

        harness.Coordinator.Recompute();

        Assert.Equal(0L, harness.Renderer.LastAlignment!.Value.OffsetTicks);

        // 地基成对：把缓冲容量从事实里归零就该对齐——
        // 证明上面那条红是缓冲项造成的，而不是别的哪一项。
        harness.Renderer.Stats = StartedStats() with { DeviceBufferFrames = 0 };
        harness.Coordinator.Recompute();
        Assert.NotEqual(0L, harness.Renderer.LastAlignment!.Value.OffsetTicks);
    }

    [Fact]
    public void FallingBack_DoesNotInterruptPlayback()
    {
        // server.hello 重发把 D 改小，退回非对齐——退回改的只是对齐参数，
        // 不得触发停播或重启。停一下再起在听感上是一次爆音加一段空白。
        var harness = new Harness();
        harness.Renderer.Stats = StartedStats();
        // 先重算再起播：开关在起播前就位（生产主路径 hello 先于起播）。
        // 本判据的领域是「退回不打断」；开关中途切换的重启另有专测，不在这里搅局。
        harness.Coordinator.Recompute();
        harness.Playback.Configure(enabled: true, targetBufferMs: 200);
        harness.Coordinator.Recompute();
        var startsBefore = harness.Renderer.StartCount;

        harness.Declaration = Declared(70);
        harness.Coordinator.Recompute();

        Assert.True(harness.Playback.IsPlaying);
        Assert.Equal(startsBefore, harness.Renderer.StartCount);
        Assert.Equal(0, harness.Renderer.StopCount);
        Assert.Equal(0L, harness.Renderer.LastAlignment!.Value.OffsetTicks);
    }

    [Fact]
    public void TheAlignmentState_SwitchesBothWaysOnTheSameCoordinator()
    {
        // 有状态的双向切换：同一台机器上 D 改小要退回、改回来要重新进入。
        // 纯函数的三次独立调用证明不了这一点——那只证明函数无缓存。
        var harness = new Harness();
        harness.Renderer.Stats = StartedStats();
        harness.Playback.Configure(enabled: true, targetBufferMs: 200);

        harness.Coordinator.Recompute();
        Assert.Equal(4242L, harness.Renderer.LastAlignment!.Value.OffsetTicks);

        harness.Declaration = Declared(70);
        harness.Coordinator.Recompute();
        Assert.Equal(0L, harness.Renderer.LastAlignment!.Value.OffsetTicks);

        harness.Declaration = Declared(300);
        harness.Coordinator.Recompute();
        Assert.Equal(4242L, harness.Renderer.LastAlignment!.Value.OffsetTicks);
    }

    [Fact]
    public void Attribution_IsLoggedOncePerTransitionNotPerRecompute()
    {
        var harness = new Harness();
        harness.Renderer.Stats = StartedStats();
        harness.Playback.Configure(enabled: true, targetBufferMs: 200);

        harness.Coordinator.Recompute();
        harness.Coordinator.Recompute();
        harness.Coordinator.Recompute();

        // 重算按探测节奏每秒来一次，逐次落日志会把「正在对齐」刷成噪声，
        // 而真正要看的那一条转移就淹没在里面。
        var entry = Assert.Single(harness.Logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("正在对齐", entry.Message);
    }

    [Fact]
    public void FallingBack_IsLoggedAsAWarningWithTheNumbers()
    {
        var harness = new Harness();
        harness.Renderer.Stats = StartedStats();
        harness.Playback.Configure(enabled: true, targetBufferMs: 200);
        harness.Coordinator.Recompute();

        harness.Declaration = Declared(70);
        harness.Coordinator.Recompute();

        // 「退回非对齐模式并记警告」的警告就落在这里；带数字，用户才知道该调哪个。
        var warning = harness.Logger.Entries[^1];
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("70", warning.Message);
        Assert.Contains("50", warning.Message);
    }

    [Fact]
    public void AlignedBeforePlaybackStarts_PrimesTheParametersButIsNotAnnounced()
    {
        // 未起播时设备事实全零，「能对齐」是个未经可行性检验的乐观结论：
        // 参数照存（起播前必须就位，起播路径会在 Start 之前下发），但不宣布——
        // 宣布了又在起播后的第一次重算退回，日志就在自我矛盾。
        var harness = new Harness();

        harness.Coordinator.Recompute();
        Assert.Empty(harness.Logger.Entries);
        Assert.Null(harness.Renderer.LastAlignment);

        harness.Playback.Configure(enabled: true, targetBufferMs: 200);

        Assert.Equal((300L * 10_000, 4242L, 0L), harness.Renderer.LastAlignment);
        // 开关经起播参数到达：Recompute 已把 enabled=true 存进播放服务，
        // 起播那一刻它随 Start 直达渲染器。
        Assert.True(harness.Renderer.LastStartAlignmentEnabled);
    }

    [Fact]
    public void BudgetDirectionFlip_LogsANewWarningEvenThoughTheStateIsTheSame()
    {
        // BudgetTooSmall 的两个方向共用一个状态：超上界与不足下界都落在它上面。
        // 方向互换若只按状态去重就不落新日志，挂着的旧警告会把「该往哪边改」指反——
        // 预算从配得太大改过头成太小时，用户看到的仍是「超出上界」。
        var harness = new Harness { Declaration = Declared(20_000) };
        harness.Renderer.Stats = StartedStats();
        harness.Playback.Configure(enabled: true, targetBufferMs: 200);
        harness.Coordinator.Recompute();

        var overCap = harness.Logger.Entries[^1];
        Assert.Equal(LogLevel.Warning, overCap.Level);
        Assert.Contains("上界", overCap.Message);

        harness.Declaration = Declared(70);
        harness.Coordinator.Recompute();

        var tooSmall = harness.Logger.Entries[^1];
        Assert.Equal(LogLevel.Warning, tooSmall.Level);
        Assert.Contains("不足", tooSmall.Message);
    }

    [Fact]
    public void SwitchedOff_PushesTheSwitchAndStaysQuiet()
    {
        // 开关关着时四态归因没有听众；但 enabled=false 仍要到达渲染器——
        // 它是起播那一刻「不建执行器」的依据，如今经 Start 的参数传递：
        // 本会话起播时协调器还没下发过，默认即关；运行时三项照走 SetAlignment。
        var harness = new Harness { Enabled = false };
        harness.Renderer.Stats = StartedStats();
        harness.Playback.Configure(enabled: true, targetBufferMs: 200);

        harness.Coordinator.Recompute();

        Assert.False(harness.Renderer.LastStartAlignmentEnabled);
        Assert.NotNull(harness.Renderer.LastAlignment);
        Assert.Empty(harness.Logger.Entries);

        // 下一次起播携带的仍是协调器存下的 false——开关到达渲染器的时点就是起播。
        harness.Playback.Configure(enabled: false, targetBufferMs: 200);
        harness.Playback.Configure(enabled: true, targetBufferMs: 200);
        Assert.False(harness.Renderer.LastStartAlignmentEnabled);
    }

    [Fact]
    public void TheManualOffset_ForTheCurrentDevice_ReachesTheRendererInTicks()
    {
        // 判据按 actual 侧的符号约定写（native 文档是权威：正 = 这台设备真实出声
        // 比自动估计更晚，加在 actual_play_ticks 上，误差随之同量平移）——设置里的
        // 正毫秒必须原符号、原数量地变成渲染器收到的正 100ns。期望值用字面量，
        // 取反或错乘一个量级都在此处红。
        var harness = new Harness();
        harness.Settings.SetManualOffsetMs("dev-a", 120);
        harness.Renderer.Stats = StartedStats();
        harness.Playback.Configure(enabled: true, targetBufferMs: 200);

        harness.Coordinator.Recompute();

        Assert.Equal(1_200_000L, harness.Renderer.LastAlignment!.Value.ManualOffsetTicks);
    }

    [Fact]
    public void SwitchingDevices_ReadsTheNewDevicesOffset_WithoutARestart()
    {
        // per-device 的意义在换设备那一刻：新设备读到自己的值，没调过的设备读 0
        // 而不是上一个设备的值——沿用旧偏移看起来就像「校准丢了」。
        // 全程不得停播重启：偏移走 ConfigureAlignment 播放中即时生效那条路。
        var harness = new Harness();
        harness.Settings.SetManualOffsetMs("dev-a", 120);
        harness.Settings.SetManualOffsetMs("dev-b", -80);
        harness.Renderer.Stats = StartedStats();
        // 开关在起播前就位（hello 先于起播的生产主路径），全程不该出现中途切换。
        harness.Coordinator.Recompute();
        harness.Playback.Configure(enabled: true, targetBufferMs: 200);

        harness.Coordinator.Recompute();
        Assert.Equal(1_200_000L, harness.Renderer.LastAlignment!.Value.ManualOffsetTicks);
        var startsBefore = harness.Renderer.StartCount;

        harness.DeviceId = "dev-b";
        harness.Coordinator.Recompute();
        Assert.Equal(-800_000L, harness.Renderer.LastAlignment!.Value.ManualOffsetTicks);

        harness.DeviceId = "dev-never-tuned";
        harness.Coordinator.Recompute();
        Assert.Equal(0L, harness.Renderer.LastAlignment!.Value.ManualOffsetTicks);

        Assert.Equal(startsBefore, harness.Renderer.StartCount);
        Assert.Equal(0, harness.Renderer.StopCount);
        Assert.True(harness.Playback.IsPlaying);
    }

    [Fact]
    public void AnInfeasibleBudget_StillDeliversTheManualOffset()
    {
        // 手动偏移是当前设备的硬件尾段这个事实，不是可行性结论：判不过归零的是
        // D 与 offset（外环的目标），设备的尾段不因预算不够而改变。native 的误差
        // 计算被 offset 可用性闸住，判不过时这个值无处施力，留着它只是让恢复对齐
        // 的那一刻少一次参数摆动。
        var harness = new Harness { Declaration = Declared(70) };
        harness.Settings.SetManualOffsetMs("dev-a", 120);
        harness.Renderer.Stats = StartedStats();
        harness.Playback.Configure(enabled: true, targetBufferMs: 200);

        harness.Coordinator.Recompute();

        Assert.Equal((0L, 0L, 1_200_000L), harness.Renderer.LastAlignment);
    }

    /// <summary>贴边数据：目标深度钉在指定值、外环误差为指定微秒数，其余同已起播事实。</summary>
    private static AudioRenderStats BrinkStats(int targetMs, long errorUs) =>
        StartedStats() with { TargetMsCurrent = targetMs, PlayTimeErrorUs = errorUs };

    /// <summary>饱和进入告警条数。恢复走 Information，不会混进来。</summary>
    private static int SaturationWarnings(ListLogger logger) => logger.Entries.Count(
        entry => entry.Level == LogLevel.Warning && entry.Message.Contains("外环饱和"));

    private static int SaturationRecoveries(ListLogger logger) =>
        logger.Entries.Count(entry => entry.Message.Contains("外环饱和解除"));

    [Fact]
    public void Saturation_IsLoggedOnEntryAndRecovery_NotPerRecompute()
    {
        // 转移语义四拍：进入一条、中间静默、恢复一条、再进入再记。重算按探测节奏
        // 每秒一次，不去重的话一次真饱和就是每秒一条同样的警告，真信号被自己刷没。
        var harness = new Harness();
        harness.Renderer.Stats = BrinkStats(1_000, 12_000);

        harness.Coordinator.Recompute();
        harness.Coordinator.Recompute();
        harness.Coordinator.Recompute();
        Assert.Equal(1, SaturationWarnings(harness.Logger));

        harness.Renderer.Stats = BrinkStats(400, 1_000);
        harness.Coordinator.Recompute();
        harness.Coordinator.Recompute();
        Assert.Equal(1, SaturationRecoveries(harness.Logger));
        Assert.Equal(1, SaturationWarnings(harness.Logger));

        harness.Renderer.Stats = BrinkStats(1_000, 12_000);
        harness.Coordinator.Recompute();
        Assert.Equal(2, SaturationWarnings(harness.Logger));
    }

    [Fact]
    public void AtTheBound_TheErrorBudgetBoundary_IsAPair()
    {
        // 单端预算 5ms 是严格大于的边：恰为 5000us 时外环只是恰好收敛到边界，
        // 不是故障；越线一微秒才是「贴边且误差仍大」。取下界与负误差，
        // 上界侧与正误差由进入判据那条盖住，绝对值被吞或比较方向取反都在此红。
        var harness = new Harness();
        harness.Renderer.Stats = BrinkStats(50, -5_000);
        harness.Coordinator.Recompute();
        Assert.Equal(0, SaturationWarnings(harness.Logger));

        harness.Renderer.Stats = BrinkStats(50, -5_001);
        harness.Coordinator.Recompute();
        Assert.Equal(1, SaturationWarnings(harness.Logger));
    }

    [Fact]
    public void OffTheBound_WithALargeError_IsNotSaturation()
    {
        // 外环还有行程时误差大是它正在修的常态，不是饱和——缺贴边这一腿的实现
        // 会把每次瞬态都当硬重置前兆告警。
        var harness = new Harness();
        harness.Renderer.Stats = BrinkStats(400, 12_000);

        harness.Coordinator.Recompute();

        Assert.Equal(0, SaturationWarnings(harness.Logger));
    }

    [Fact]
    public void NotAligned_WithTheSameBrinkFacts_IsNotJudged()
    {
        // 与进入判据只差对齐态一个条件：同样贴上界、同样 12ms 误差，仅对时不可用
        // （NoClockOffset）。未对齐时 stats 是上一段对齐留下的残值，拿残值判饱和
        // 是拿旧世界的读数吓唬现世界。
        var harness = new Harness { WireOffset = (false, 0L) };
        harness.Renderer.Stats = BrinkStats(1_000, 12_000);

        harness.Coordinator.Recompute();

        Assert.Equal(0, SaturationWarnings(harness.Logger));
    }

    [Fact]
    public void SwitchedOff_WithBrinkFacts_JudgesNothingButStillSnapshots()
    {
        // 开关关时零参与：饱和不判、告警不发，日志必须整体为空。快照照更新——
        // 状态行还要回答「为什么没在对齐」，Enabled 位随组带出，显示方据此改口。
        var harness = new Harness { Enabled = false };
        harness.Renderer.Stats = BrinkStats(1_000, 12_000);

        harness.Coordinator.Recompute();

        Assert.Empty(harness.Logger.Entries);
        var snapshot = harness.Coordinator.LastDecision;
        Assert.NotNull(snapshot);
        Assert.False(snapshot!.Value.Enabled);
    }

    [Fact]
    public void TheDecisionSnapshot_FollowsEachRecompute()
    {
        // 防真空：先后两次给出不同结论，快照必须跟随——恒首次值的实现在第二段红。
        var harness = new Harness();
        harness.Renderer.Stats = StartedStats() with
        {
            TargetMsCurrent = 180, PlayTimeErrorUs = -7_200
        };

        Assert.Null(harness.Coordinator.LastDecision);

        harness.Coordinator.Recompute();
        var aligned = harness.Coordinator.LastDecision;
        Assert.NotNull(aligned);
        Assert.Equal(MediaLinkAlignmentState.Aligned, aligned!.Value.State);
        Assert.Equal("正在对齐播放", aligned.Value.Reason);
        // 页面要显示的数字随组带出，与判定同一次读取——页面不重算归因也不补读 stats。
        Assert.Equal(180, aligned.Value.TargetMsCurrent);
        Assert.Equal(-7_200L, aligned.Value.PlayTimeErrorUs);
        Assert.Equal((50, 1_000), (aligned.Value.MinTargetMs, aligned.Value.MaxTargetMs));
        Assert.True(aligned.Value.Enabled);
        Assert.True(aligned.Value.HasStarted);

        harness.Declaration = Declared(70);
        harness.Coordinator.Recompute();
        var fallen = harness.Coordinator.LastDecision;
        Assert.Equal(MediaLinkAlignmentState.BudgetTooSmall, fallen!.Value.State);
        Assert.Contains("不足", fallen.Value.Reason);
    }

    [Fact]
    public async Task TheSnapshot_StaysPairwiseConsistent_UnderConcurrentReads()
    {
        // 简单交错：写线程在两个结论之间摆动，读线程裸读快照。State 与 Reason
        // 同锁同写，读到的对子必须自洽——快照写点漏出锁或属性绕开锁时这里撕开。
        var harness = new Harness();
        harness.Renderer.Stats = StartedStats();
        var stop = false;
        var violations = 0;
        var reader = Task.Run(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                if (harness.Coordinator.LastDecision is not { } snapshot)
                {
                    continue;
                }

                var consistent = snapshot.State switch
                {
                    MediaLinkAlignmentState.Aligned => snapshot.Reason == "正在对齐播放",
                    MediaLinkAlignmentState.BudgetTooSmall => snapshot.Reason.Contains("不足"),
                    _ => false
                };
                if (!consistent)
                {
                    Interlocked.Increment(ref violations);
                }
            }
        });

        for (var i = 0; i < 2_000; i++)
        {
            harness.Declaration = Declared(i % 2 == 0 ? 300 : 70);
            harness.Coordinator.Recompute();
        }

        Volatile.Write(ref stop, true);
        await reader;
        Assert.Equal(0, violations);
    }
}
