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
        public (bool Enabled, long DTicks, long OffsetTicks, long ManualOffsetTicks)? LastAlignment
        {
            get;
            private set;
        }

        public AudioRenderStats Stats { get; set; }

        public void SetAlignment(bool enabled, long dTicks, long offsetTicks, long manualOffsetTicks) =>
            LastAlignment = (enabled, dTicks, offsetTicks, manualOffsetTicks);

        public void Start(int targetBufferMs) => StartCount++;

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
        Assert.Equal((true, 300L * 10_000, 4242L, 0L), harness.Renderer.LastAlignment);
    }

    [Fact]
    public void AnInfeasibleBudget_KeepsTheSwitchButZeroesTheTargets()
    {
        // 预算 70 减设备延迟 10 与缓冲 20 后剩 40，装不下最小缓冲 50。开关照下发——
        // 它决定起播那一刻建不建执行器；但 D 与 offset 必须归零让外环不动，
        // 否则外环拿着一个已被判死的目标继续走步，正是「假装对齐」的形态。
        var harness = new Harness { Declaration = Declared(70) };
        harness.Renderer.Stats = StartedStats();
        harness.Playback.Configure(enabled: true, targetBufferMs: 200);

        harness.Coordinator.Recompute();

        Assert.Equal((true, 0L, 0L, 0L), harness.Renderer.LastAlignment);
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

        Assert.Equal((true, 300L * 10_000, 4242L, 0L), harness.Renderer.LastAlignment);
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
        // 它是起播那一刻「不建执行器」的依据。
        var harness = new Harness { Enabled = false };
        harness.Renderer.Stats = StartedStats();
        harness.Playback.Configure(enabled: true, targetBufferMs: 200);

        harness.Coordinator.Recompute();

        Assert.False(harness.Renderer.LastAlignment!.Value.Enabled);
        Assert.Empty(harness.Logger.Entries);
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

        Assert.Equal((true, 0L, 0L, 1_200_000L), harness.Renderer.LastAlignment);
    }
}
