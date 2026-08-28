using MediaIsland.Services.Audio;
using MediaIsland.Services.Audio.Playback;
using Xunit;

namespace MediaIsland.Tests.Audio;

/// <summary>
/// 播放装饰器。它的全部内容是「这一帧该给谁」，而给错的表现极其隐蔽：
/// 播放开着却把帧同时给了内层，频谱就会领先耳朵一个抖动缓冲的深度；
/// 播放起不来却不回落，频谱会跟着一起死掉。
/// </summary>
public class AudioPlaybackServiceTests
{
    private sealed class RecordingSubmitter : IAudioFrameSubmitter
    {
        public List<AudioFrame> Frames { get; } = [];

        public void Submit(AudioFrame frame) => Frames.Add(frame);
    }

    /// <summary>
    /// 第一个进入 <see cref="Submit"/> 的调用卡住不返回，后续调用立即通过。用来把
    /// 「两个线程同时进内层」变成确定性事件，而不是靠压力测试碰运气。
    ///
    /// 只挡第一个是判据成立的关键：若后续调用也挡，那么「第二个提交没完成」
    /// 在有锁与无锁两种实现下都成立，这条测试就一点区分力也没有。
    /// </summary>
    private sealed class BlockingSubmitter : IAudioFrameSubmitter
    {
        private readonly ManualResetEventSlim _release = new(false);
        private int _calls;

        public ManualResetEventSlim FirstEntered { get; } = new(false);

        public int Completed;

        public void Submit(AudioFrame frame)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                FirstEntered.Set();
                _release.Wait(TimeSpan.FromSeconds(10));
            }

            Interlocked.Increment(ref Completed);
        }

        public void Release() => _release.Set();
    }

    private sealed class FakeRenderer : IAudioRenderer
    {
        public bool IsAvailable { get; set; } = true;
        public string? FailureReason { get; set; }
        public bool ThrowOnStart { get; set; }
        public List<(byte[] Pcm, long SenderTicks)> Pushed { get; } = [];
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public int? LastTargetMs { get; private set; }

        /// <summary>按发生顺序记下的调用名。顺序本身是契约，故不能只记次数。</summary>
        public List<string> Calls { get; } = [];

        /// <summary>最近一次 Start 携带的对齐开关。null 表示还没起播过。</summary>
        public bool? LastStartAlignmentEnabled { get; private set; }

        public (long DTicks, long OffsetTicks, long ManualOffsetTicks)? LastAlignment
        {
            get;
            private set;
        }

        public event Action<AudioFrame>? FramePlayed;

        public void Start(int targetBufferMs, bool alignmentEnabled)
        {
            if (ThrowOnStart)
            {
                throw new InvalidOperationException("设备被独占");
            }

            StartCount++;
            LastTargetMs = targetBufferMs;
            LastStartAlignmentEnabled = alignmentEnabled;
            Calls.Add("Start");
        }

        public void SetAlignment(long dTicks, long offsetTicks, long manualOffsetTicks)
        {
            LastAlignment = (dTicks, offsetTicks, manualOffsetTicks);
            Calls.Add("SetAlignment");
        }

        public void Stop()
        {
            StopCount++;
            Calls.Add("Stop");
        }

        public void Push(byte[] pcm, long senderTicks) => Pushed.Add((pcm, senderTicks));

        /// <summary>可写：Task 4 与 Task 5 的判据要让它返回特定统计。</summary>
        public AudioRenderStats Stats { get; set; }

        public AudioRenderStats ReadStats() => Stats;

        /// <summary>目标深度区间。真实实现取自 native 常量，这里可写以驱动判定边界。</summary>
        public (int MinTargetMs, int MaxTargetMs) DepthBounds { get; set; } = (50, 1000);

        public (int MinTargetMs, int MaxTargetMs) TargetDepthBounds() => DepthBounds;

        public void EmitPlayed(AudioFrame frame) => FramePlayed?.Invoke(frame);

        public void Dispose() { }
    }

    private static AudioFrame Frame(params short[] samples)
    {
        var pcm = new byte[samples.Length * sizeof(short)];
        Buffer.BlockCopy(samples, 0, pcm, 0, pcm.Length);
        return new AudioFrame(pcm, 0, 48_000, 2, IsSilent: false);
    }

    [Fact]
    public void Starting_CarriesTheAlignmentSwitchAndPushesRuntimeParameters()
    {
        // 第 7 期的顺序判据（SetAlignment 先于 Start）随时序契约一起消失：enabled 如今
        // 是 Start 的参数，「起播前设好」由类型系统保证，时序错误写不出来。这里钉的是
        // 新契约的两半——Start 携带 ConfigureAlignment 存下的开关值（恒传 false 的实现
        // 在此红），运行时三项照走 SetAlignment 且在起播时已到达。
        var inner = new RecordingSubmitter();
        var renderer = new FakeRenderer();
        using var service = new AudioPlaybackService(inner, renderer);

        service.ConfigureAlignment(
            enabled: true, dTicks: 3_000_000, offsetTicks: 7, manualOffsetTicks: -5);
        service.Configure(enabled: true, targetBufferMs: 200);

        Assert.True(renderer.LastStartAlignmentEnabled);
        Assert.Equal((3_000_000L, 7L, -5L), renderer.LastAlignment);
        // 会话起播值同时被记录：中途切换的重启判断要比对它。
        Assert.True(service.SessionAlignmentEnabled);
    }

    [Fact]
    public void Starting_WithoutAlignmentConfigured_CarriesFalse()
    {
        // 反向成对：没人开过对齐时 Start 必须携带 false——恒传 true 的实现在此红，
        // 而那种实现会让每个 48k 端点都白付重采样器的代价（48k 不再 bit-exact）。
        var inner = new RecordingSubmitter();
        var renderer = new FakeRenderer();
        using var service = new AudioPlaybackService(inner, renderer);

        service.Configure(enabled: true, targetBufferMs: 200);

        Assert.False(renderer.LastStartAlignmentEnabled);
        Assert.False(service.SessionAlignmentEnabled);
    }

    [Fact]
    public void ConfiguringAlignmentWhilePlaying_WithTheSameSwitch_PushesWithoutRestart()
    {
        // offset 随对时结果每秒都可能变，播放中必须能改且只改参数——同值开关
        // （对时下发走的就是这条路）绝不触发停播重启，否则探测节奏变成每秒重启。
        var inner = new RecordingSubmitter();
        var renderer = new FakeRenderer();
        using var service = new AudioPlaybackService(inner, renderer);
        service.ConfigureAlignment(
            enabled: true, dTicks: 3_000_000, offsetTicks: 1, manualOffsetTicks: 0);
        service.Configure(enabled: true, targetBufferMs: 200);
        var startsBefore = renderer.StartCount;

        service.ConfigureAlignment(
            enabled: true, dTicks: 3_000_000, offsetTicks: 42, manualOffsetTicks: 0);
        service.ConfigureAlignment(
            enabled: true, dTicks: 3_000_000, offsetTicks: 43, manualOffsetTicks: -5);

        Assert.Equal((3_000_000L, 43L, -5L), renderer.LastAlignment);
        Assert.Equal(startsBefore, renderer.StartCount);
        Assert.Equal(0, renderer.StopCount);
        Assert.Equal("SetAlignment", renderer.Calls[^1]);
        Assert.True(service.SessionAlignmentEnabled);
    }

    [Fact]
    public void FlippingTheAlignmentSwitchOn_WhilePlaying_RestartsOnceCarryingIt()
    {
        // enabled 并进 render_start 之后，重启是中途切换唯一可能的生效方式。
        // 防真空：先钉 stop 与新 start 各发生一次，再断顺序、开关与深度保持。
        var inner = new RecordingSubmitter();
        var renderer = new FakeRenderer();
        using var service = new AudioPlaybackService(inner, renderer);
        service.Configure(enabled: true, targetBufferMs: 200);
        Assert.False(service.SessionAlignmentEnabled);
        var callsBefore = renderer.Calls.Count;

        service.ConfigureAlignment(
            enabled: true, dTicks: 3_000_000, offsetTicks: 7, manualOffsetTicks: 0);

        Assert.Equal(1, renderer.StopCount);
        Assert.Equal(2, renderer.StartCount);
        Assert.Equal(["Stop", "SetAlignment", "Start"], renderer.Calls.Skip(callsBefore));
        Assert.True(renderer.LastStartAlignmentEnabled);
        Assert.True(service.SessionAlignmentEnabled);
        Assert.Equal(200, renderer.LastTargetMs);
        Assert.True(service.IsPlaying);
    }

    [Fact]
    public void FlippingTheAlignmentSwitchOff_WhilePlaying_RestartsCarryingFalse()
    {
        // 成对的反向：关掉也要一次重启把 false 带进新会话，
        // 否则内环的执行器建着不拆，关了开关还在走时间轴。
        var inner = new RecordingSubmitter();
        var renderer = new FakeRenderer();
        using var service = new AudioPlaybackService(inner, renderer);
        service.ConfigureAlignment(
            enabled: true, dTicks: 3_000_000, offsetTicks: 7, manualOffsetTicks: 0);
        service.Configure(enabled: true, targetBufferMs: 200);
        Assert.True(service.SessionAlignmentEnabled);

        service.ConfigureAlignment(
            enabled: false, dTicks: 3_000_000, offsetTicks: 7, manualOffsetTicks: 0);

        Assert.Equal(2, renderer.StartCount);
        Assert.False(renderer.LastStartAlignmentEnabled);
        Assert.False(service.SessionAlignmentEnabled);
        Assert.True(service.IsPlaying);
    }

    [Fact]
    public void FlippingTheAlignmentSwitch_WhileNotPlaying_DoesNotTouchTheRenderer()
    {
        // 未在播时开关不一致是常态（会话值是上一会话的残值），零动作：
        // 值存下等下一次起播，不许有任何渲染器调用。
        var inner = new RecordingSubmitter();
        var renderer = new FakeRenderer();
        using var service = new AudioPlaybackService(inner, renderer);

        service.ConfigureAlignment(
            enabled: true, dTicks: 3_000_000, offsetTicks: 7, manualOffsetTicks: 0);

        Assert.Empty(renderer.Calls);
        Assert.Equal(0, renderer.StartCount);
        Assert.Equal(0, renderer.StopCount);
    }

    [Fact]
    public void SwitchFlipRestart_OnStartFailure_TakesTheLastErrorPath()
    {
        // 中途切换的重启失败与起播失败同形态：停在关闭态、原因可读。
        var inner = new RecordingSubmitter();
        var renderer = new FakeRenderer();
        using var service = new AudioPlaybackService(inner, renderer);
        service.Configure(enabled: true, targetBufferMs: 200);
        renderer.ThrowOnStart = true;

        service.ConfigureAlignment(
            enabled: true, dTicks: 3_000_000, offsetTicks: 7, manualOffsetTicks: 0);

        Assert.False(service.IsPlaying);
        Assert.Contains("设备被独占", service.LastError);
    }


    [Fact]
    public void DeviceChangeRestart_StopsThenStartsWithTheStagedRequest()
    {
        // 设备变化后设备事实全体作废，响应是停播重启。防真空：先钉「确实各发生了一次
        // stop 与一次新 start」，再断顺序与参数——只断顺序的话空实现也能通过。
        var inner = new RecordingSubmitter();
        var renderer = new FakeRenderer();
        using var service = new AudioPlaybackService(inner, renderer);
        service.ConfigureAlignment(
            enabled: true, dTicks: 3_000_000, offsetTicks: 7, manualOffsetTicks: -5);
        service.Configure(enabled: true, targetBufferMs: 250);
        var callsBefore = renderer.Calls.Count;

        service.RestartForDeviceChange();

        Assert.Equal(1, renderer.StopCount);
        Assert.Equal(2, renderer.StartCount);
        // 重启的调用序：停旧会话 → 起播前补发运行时三项 → 新 Start。
        Assert.Equal(
            ["Stop", "SetAlignment", "Start"],
            renderer.Calls.Skip(callsBefore));
        // target 与 enabled 取暂存请求值，不是默认值：换设备不该顺手改掉用户的配置。
        Assert.Equal(250, renderer.LastTargetMs);
        Assert.True(renderer.LastStartAlignmentEnabled);
        Assert.True(service.SessionAlignmentEnabled);
        Assert.True(service.IsPlaying);
    }

    [Fact]
    public void DeviceChangeRestart_WithoutAnActiveRequest_DoesNothing()
    {
        // 没人请求播放时设备变化与本服务无关：不许有任何渲染器调用，
        // 否则关着播放也会被设备切换惊起。
        var inner = new RecordingSubmitter();
        var renderer = new FakeRenderer();
        using var service = new AudioPlaybackService(inner, renderer);

        service.RestartForDeviceChange();

        Assert.Empty(renderer.Calls);
        Assert.Equal(0, renderer.StopCount);
        Assert.False(service.IsPlaying);
    }

    [Fact]
    public void DeviceChangeRestart_AfterAnEarlierFailure_RetriesTheStart()
    {
        // 错误处理约定：重启失败后播放停在关闭态，watcher 继续跑，设备再变仍会重试。
        // 重试的依据是请求值还开着——比「在播」的话一次失败就永久聋。
        var inner = new RecordingSubmitter();
        var renderer = new FakeRenderer { ThrowOnStart = true };
        using var service = new AudioPlaybackService(inner, renderer);
        service.Configure(enabled: true, targetBufferMs: 200);
        Assert.False(service.IsPlaying);

        renderer.ThrowOnStart = false;
        service.RestartForDeviceChange();

        Assert.True(service.IsPlaying);
        Assert.Null(service.LastError);
    }

    [Fact]
    public void DeviceChangeRestart_OnFailure_TakesTheExistingLastErrorPath()
    {
        // 重启失败与起播失败必须是同一个错误形态：停在关闭态、原因可读、直连回落。
        var inner = new RecordingSubmitter();
        var renderer = new FakeRenderer();
        using var service = new AudioPlaybackService(inner, renderer);
        service.Configure(enabled: true, targetBufferMs: 200);
        renderer.ThrowOnStart = true;

        service.RestartForDeviceChange();

        Assert.False(service.IsPlaying);
        Assert.Contains("设备被独占", service.LastError);
        service.Submit(Frame(1, -1));
        Assert.Single(inner.Frames);
    }

    [Fact]
    public void PlaybackOff_ForwardsStraightToInner()
    {
        var inner = new RecordingSubmitter();
        var renderer = new FakeRenderer();
        using var service = new AudioPlaybackService(inner, renderer);

        service.Submit(Frame(1, -1));

        Assert.Single(inner.Frames);
        Assert.Empty(renderer.Pushed);
        Assert.False(service.IsPlaying);
    }

    [Fact]
    public void PlaybackOn_SendsToRendererAndNotToInner()
    {
        var inner = new RecordingSubmitter();
        var renderer = new FakeRenderer();
        using var service = new AudioPlaybackService(inner, renderer);

        service.Configure(enabled: true, targetBufferMs: 200);
        service.Submit(Frame(1, -1));

        // 地基先钉住：renderer 确实收到了。只断言 inner 为空的话，
        // 「什么都没发生」也会通过。
        Assert.Single(renderer.Pushed);
        Assert.Equal(1, renderer.StartCount);
        Assert.Equal(200, renderer.LastTargetMs);
        Assert.Empty(inner.Frames);
        Assert.True(service.IsPlaying);
    }

    [Fact]
    public void PlayedFrames_ReachInnerWhilePlaying()
    {
        var inner = new RecordingSubmitter();
        var renderer = new FakeRenderer();
        using var service = new AudioPlaybackService(inner, renderer);
        service.Configure(enabled: true, targetBufferMs: 200);

        renderer.EmitPlayed(Frame(9, -9));

        // 这是音画对齐的全部机制：频谱算的是刚送进扬声器的那些字节。
        var only = Assert.Single(inner.Frames);
        Assert.Equal(Frame(9, -9).Pcm, only.Pcm);
    }

    [Fact]
    public void UnavailableRenderer_FallsBackToDirect()
    {
        // 不回落的后果是打开播放开关会让频谱一起死掉，
        // 而用户的心智模型里这两件事无关，排查方向会指向频谱组件。
        var inner = new RecordingSubmitter();
        var renderer = new FakeRenderer { IsAvailable = false, FailureReason = "找不到原生库" };
        using var service = new AudioPlaybackService(inner, renderer);

        service.Configure(enabled: true, targetBufferMs: 200);
        service.Submit(Frame(1, -1));

        Assert.Single(inner.Frames);
        Assert.Empty(renderer.Pushed);
        Assert.False(service.IsPlaying);
        Assert.Equal("找不到原生库", service.LastError);
    }

    [Fact]
    public void StartFailure_FallsBackToDirectAndRecordsReason()
    {
        var inner = new RecordingSubmitter();
        var renderer = new FakeRenderer { ThrowOnStart = true };
        using var service = new AudioPlaybackService(inner, renderer);

        service.Configure(enabled: true, targetBufferMs: 200);
        service.Submit(Frame(1, -1));

        Assert.Single(inner.Frames);
        Assert.False(service.IsPlaying);
        Assert.Contains("设备被独占", service.LastError);
    }

    [Fact]
    public void TurningPlaybackOff_StopsRendererAndResumesDirect()
    {
        var inner = new RecordingSubmitter();
        var renderer = new FakeRenderer();
        using var service = new AudioPlaybackService(inner, renderer);

        service.Configure(enabled: true, targetBufferMs: 200);
        service.Configure(enabled: false, targetBufferMs: 200);
        service.Submit(Frame(1, -1));

        Assert.Equal(1, renderer.StopCount);
        Assert.Single(inner.Frames);
        Assert.Empty(renderer.Pushed);
    }

    [Fact]
    public void RepeatedConfigure_WithSameValues_IsNoOp()
    {
        // 幂等：Configure 由设置变化与仲裁变化共同触发，会被反复调用。
        // 每次都重启 renderer 会让播放一顿一顿。
        var inner = new RecordingSubmitter();
        var renderer = new FakeRenderer();
        using var service = new AudioPlaybackService(inner, renderer);

        service.Configure(enabled: true, targetBufferMs: 200);
        service.Configure(enabled: true, targetBufferMs: 200);
        service.Configure(enabled: true, targetBufferMs: 200);

        Assert.Equal(1, renderer.StartCount);
    }

    [Fact]
    public void RepeatedConfigure_AfterStartFailure_RetriesInsteadOfLatching()
    {
        // 起播失败后请求值与实际值不一致，这是唯一能让下一次同值 Configure 重试的依据。
        // 幂等判据若只比请求值，一次失败会把播放永久卡在关闭态，而设置页显示的是开。
        var inner = new RecordingSubmitter();
        var renderer = new FakeRenderer { ThrowOnStart = true };
        using var service = new AudioPlaybackService(inner, renderer);

        service.Configure(enabled: true, targetBufferMs: 200);
        renderer.ThrowOnStart = false;
        service.Configure(enabled: true, targetBufferMs: 200);

        Assert.True(service.IsPlaying);
        Assert.Null(service.LastError);
    }

    [Fact]
    public void ChangingTargetDepth_RestartsRenderer()
    {
        // 深度变更要么丢音要么静音填充，两者都不如一次干净的重启，且这是罕见操作。
        var inner = new RecordingSubmitter();
        var renderer = new FakeRenderer();
        using var service = new AudioPlaybackService(inner, renderer);

        service.Configure(enabled: true, targetBufferMs: 200);
        service.Configure(enabled: true, targetBufferMs: 80);

        Assert.Equal(2, renderer.StartCount);
        Assert.Equal(1, renderer.StopCount);
        Assert.Equal(80, renderer.LastTargetMs);
    }

    [Fact]
    public void PlayedFrames_AfterPlaybackStops_DoNotReachInnerTwice()
    {
        // 停播后 renderer 可能还有在途回调。此时帧路径已切回直连，
        // 让在途回调继续进内层会与直连帧交错，频谱会出现时间倒流。
        var inner = new RecordingSubmitter();
        var renderer = new FakeRenderer();
        using var service = new AudioPlaybackService(inner, renderer);
        service.Configure(enabled: true, targetBufferMs: 200);
        service.Configure(enabled: false, targetBufferMs: 200);

        renderer.EmitPlayed(Frame(9, -9));

        Assert.Empty(inner.Frames);
    }

    [Fact]
    public void ConcurrentSubmits_AreSerialized()
    {
        // 停播的瞬间会真的有两个写者：渲染线程的在途回调读到的还是「在播」，
        // 而网络线程已经读到「没播」并走直连。两条都落到内层，
        // 而内层的 AudioSampleRing 明文声明「唯一写者」，Append 是读-改-写。
        //
        // 这条不是压力测试：第一个提交被挡在内层里不返回，第二个必须因此进不去。
        // 断言是「第二个没完成」，故负载再高也只会让它更成立，不会假红。
        var inner = new BlockingSubmitter();
        var renderer = new FakeRenderer();
        using var service = new AudioPlaybackService(inner, renderer);
        service.Configure(enabled: true, targetBufferMs: 200);

        // 渲染线程那一路：进了内层就卡住。
        var played = Task.Run(() => renderer.EmitPlayed(Frame(9, -9)));
        Assert.True(inner.FirstEntered.Wait(TimeSpan.FromSeconds(5)), "第一个提交应已进入内层");

        // 网络线程那一路：切回直连后提交，必须被挡在装饰器里。
        service.Configure(enabled: false, targetBufferMs: 200);
        var direct = Task.Run(() => service.Submit(Frame(1, -1)));

        Assert.False(direct.Wait(TimeSpan.FromMilliseconds(300)), "第二个提交不得与第一个重叠");
        Assert.Equal(0, Volatile.Read(ref inner.Completed));

        inner.Release();
        Assert.True(Task.WhenAll(played, direct).Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(2, Volatile.Read(ref inner.Completed));
    }

    [Fact]
    public void Dispose_StopsRenderer()
    {
        var inner = new RecordingSubmitter();
        var renderer = new FakeRenderer();
        var service = new AudioPlaybackService(inner, renderer);
        service.Configure(enabled: true, targetBufferMs: 200);

        service.Dispose();

        Assert.Equal(1, renderer.StopCount);
    }

    [Fact]
    public void MismatchedSampleRate_IsDroppedNotPushed()
    {
        var inner = new RecordingSubmitter();
        var renderer = new FakeRenderer();
        using var service = new AudioPlaybackService(inner, renderer);
        service.Configure(enabled: true, targetBufferMs: 200);

        // 地基：确认此刻确实在播放路径上，否则「没进渲染器」由不播放造成，
        // 与校验生效无从区分。
        Assert.True(service.IsPlaying);

        service.Submit(new AudioFrame(new byte[8], 0, SampleRate: 44_100, Channels: 2, IsSilent: false));

        Assert.Empty(renderer.Pushed);
        Assert.Equal(1, service.RejectedFrameCount);
    }

    [Fact]
    public void MismatchedChannelCount_IsDroppedNotPushed()
    {
        var inner = new RecordingSubmitter();
        var renderer = new FakeRenderer();
        using var service = new AudioPlaybackService(inner, renderer);
        service.Configure(enabled: true, targetBufferMs: 200);
        Assert.True(service.IsPlaying);

        service.Submit(new AudioFrame(new byte[8], 0, SampleRate: 48_000, Channels: 1, IsSilent: false));

        Assert.Empty(renderer.Pushed);
        Assert.Equal(1, service.RejectedFrameCount);
    }

    [Fact]
    public void MatchingFrame_StillReachesTheRenderer()
    {
        // 负向条件恰好满足的防线：若校验写成恒真，上面两条照样绿，
        // 而那会让播放彻底静音。
        var inner = new RecordingSubmitter();
        var renderer = new FakeRenderer();
        using var service = new AudioPlaybackService(inner, renderer);
        service.Configure(enabled: true, targetBufferMs: 200);

        service.Submit(new AudioFrame(new byte[8], 0, 48_000, 2, IsSilent: false));

        Assert.Single(renderer.Pushed);
        Assert.Equal(0, service.RejectedFrameCount);
    }

    [Fact]
    public void Submit_PassesTheSenderTimelineThroughToTheRenderer()
    {
        // 发送端时刻是对齐播放算目标出声时刻的输入。这里若被吞成 0（「本帧没有时刻」
        // 的哨兵），native 侧永远走不带时间轴的原路径——声音照出、判据照绿，只是永远对不齐。
        var inner = new RecordingSubmitter();
        var renderer = new FakeRenderer();
        using var service = new AudioPlaybackService(inner, renderer);
        service.Configure(enabled: true, targetBufferMs: 200);
        Assert.True(service.IsPlaying);

        service.Submit(new AudioFrame(
            new byte[8], 0, 48_000, 2, IsSilent: false,
            SenderTimelineTicks100Ns: 17_000_000_000_000_000));

        Assert.Equal(17_000_000_000_000_000, Assert.Single(renderer.Pushed).SenderTicks);
    }

    [Fact]
    public void Submit_WithoutASenderTimeline_PushesTheNoTimestampSentinel()
    {
        // 本机采集与老对端的帧不带时刻。0 必须原样到达渲染器——它是「走原路径」的开关，
        // 在这里被替换成任何别的值都会让不该走时间轴的帧走进时间轴。
        // QPC 给非零值是地基：帧上若只有 0 可抄，「拿别的字段编一个时刻」的错法
        // 编出来还是 0，这条判据就分辨不出它。
        var inner = new RecordingSubmitter();
        var renderer = new FakeRenderer();
        using var service = new AudioPlaybackService(inner, renderer);
        service.Configure(enabled: true, targetBufferMs: 200);

        service.Submit(new AudioFrame(new byte[8], 987_654_321, 48_000, 2, IsSilent: false));

        Assert.Equal(0, Assert.Single(renderer.Pushed).SenderTicks);
    }

    [Fact]
    public void MismatchedFrame_WhileNotPlaying_StillReachesInner()
    {
        // 校验的约束来自渲染器，不是来自帧本身。不播放时走直连，
        // 可视化按帧携带的采样率自己处理——在这里也拦掉会让非 48k 的源连频谱都没有。
        var inner = new RecordingSubmitter();
        var renderer = new FakeRenderer();
        using var service = new AudioPlaybackService(inner, renderer);

        service.Submit(new AudioFrame(new byte[8], 0, 44_100, 2, IsSilent: false));

        Assert.Single(inner.Frames);
        Assert.Equal(0, service.RejectedFrameCount);
    }

    [Fact]
    public void OutputLatency_IsZeroWhileNotPlaying()
    {
        // 不出声就没有本机引入的延迟。此时媒体时钟对齐的是对方的实时位置，
        // 那本身就是对的——减一个缓冲深度反而会把歌词推到听觉后面。
        var service = new AudioPlaybackService(new RecordingSubmitter(), new FakeRenderer());
        using var _ = service;

        Assert.Equal(TimeSpan.Zero, service.OutputLatency);
    }

    [Fact]
    public void OutputLatency_EqualsTargetDepthWhilePlaying()
    {
        var renderer = new FakeRenderer();
        using var service = new AudioPlaybackService(new RecordingSubmitter(), renderer);

        service.Configure(enabled: true, targetBufferMs: 200);

        // 地基：先钉住真的在播。否则「延迟为 200」与「压根没起播但读到了请求值」长得一样。
        Assert.True(service.IsPlaying);
        Assert.Equal(TimeSpan.FromMilliseconds(200), service.OutputLatency);
    }

    [Fact]
    public void OutputLatency_FollowsDepthChanges()
    {
        var renderer = new FakeRenderer();
        using var service = new AudioPlaybackService(new RecordingSubmitter(), renderer);

        service.Configure(enabled: true, targetBufferMs: 200);
        service.Configure(enabled: true, targetBufferMs: 500);

        Assert.Equal(TimeSpan.FromMilliseconds(500), service.OutputLatency);
    }

    [Fact]
    public void OutputLatency_IsZeroWhenRendererUnavailable()
    {
        // 这一条是选 IsPlaying 而不是 RequestedIsEnabled 的全部理由。
        // native 缺失时请求为开、帧走直连、没有任何额外延迟；
        // 若按请求值报 200ms，歌词会被推到听觉后面 200ms，比不补偿更坏。
        var renderer = new FakeRenderer { IsAvailable = false, FailureReason = "native 缺失" };
        using var service = new AudioPlaybackService(new RecordingSubmitter(), renderer);

        service.Configure(enabled: true, targetBufferMs: 200);

        // 地基：请求确实是开的，只是没播起来。少了这一条，本判据与
        // 「Configure 压根没被调用」无从区分。
        Assert.True(service.RequestedIsEnabled);
        Assert.False(service.IsPlaying);
        Assert.Equal(TimeSpan.Zero, service.OutputLatency);
    }

    [Fact]
    public void OutputLatency_ReturnsToZeroAfterStop()
    {
        var renderer = new FakeRenderer();
        using var service = new AudioPlaybackService(new RecordingSubmitter(), renderer);

        service.Configure(enabled: true, targetBufferMs: 200);
        Assert.Equal(TimeSpan.FromMilliseconds(200), service.OutputLatency);

        service.Configure(enabled: false, targetBufferMs: 200);

        Assert.Equal(TimeSpan.Zero, service.OutputLatency);
    }

    [Fact]
    public void ReadAlignmentFacts_CarriesTheOuterLoopPairFromStats()
    {
        // 诊断面的两笔输入（目标深度当前值、外环误差）必须与其余事实同源自同一次
        // ReadStats——漏接哪个，饱和判定就拿着零去判「贴边」或「误差在预算内」。
        // 期望值取不对称字面量且误差为负：两字段串位或符号被吞都在此红。
        var renderer = new FakeRenderer
        {
            Stats = new AudioRenderStats(
                RingFrames: 0, UnderrunCount: 0, HardResetCount: 0, DeviceFramesRendered: 0,
                DeviceSampleRate: 48_000, ResampleRatioPpm: 0,
                TargetMsCurrent: 180, PlayTimeErrorUs: -7_200)
        };
        using var service = new AudioPlaybackService(new RecordingSubmitter(), renderer);

        var facts = service.ReadAlignmentFacts();

        Assert.Equal(180, facts.TargetMsCurrent);
        Assert.Equal(-7_200L, facts.PlayTimeErrorUs);
    }
}
