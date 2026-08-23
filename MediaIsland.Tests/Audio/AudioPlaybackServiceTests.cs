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
        public List<byte[]> Pushed { get; } = [];
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public int? LastTargetMs { get; private set; }

        public event Action<AudioFrame>? FramePlayed;

        public void Start(int targetBufferMs)
        {
            if (ThrowOnStart)
            {
                throw new InvalidOperationException("设备被独占");
            }

            StartCount++;
            LastTargetMs = targetBufferMs;
        }

        public void Stop() => StopCount++;

        public void Push(byte[] pcm, long senderTicks) => Pushed.Add(pcm);

        /// <summary>可写：Task 4 与 Task 5 的判据要让它返回特定统计。</summary>
        public AudioRenderStats Stats { get; set; }

        public AudioRenderStats ReadStats() => Stats;

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
}
