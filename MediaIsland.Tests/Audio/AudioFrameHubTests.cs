using MediaIsland.Services.Audio;
using Xunit;

namespace MediaIsland.Tests.Audio;

/// <summary>
/// PCM 帧的分发与采集按需启停。
///
/// 该层刻意不知晓 MediaLink 协议存在——帧源、分发、FFT 都不应依赖协议，
/// MediaLink 只是 Hub 的一个 sink 加一个 source。
///
/// 采集启停用显式的需求状态而非 sink 引用计数：采集需求来自协议层（谁订阅了、谁请求了），
/// 不是「有几个 sink」。第 3 期的可视化 sink 会常驻，但它不该让采集永远开着。
/// </summary>
public class AudioFrameHubTests
{
    private static AudioFrame Frame(byte tag = 1) =>
        new([tag, tag], QpcPosition100Ns: 0, SampleRate: 48000, Channels: 2, IsSilent: false);

    /// <summary>建立采集需求后的 Hub。分发只在采集中进行，故分发类测试都从这里起步。</summary>
    private static async Task<(AudioFrameHub Hub, FakeAudioFrameSource Source)> CapturingHubAsync()
    {
        var source = new FakeAudioFrameSource();
        var hub = new AudioFrameHub(source);
        await hub.SetCaptureDemandAsync(true, CancellationToken.None);
        return (hub, source);
    }

    // ---- 分发 ----

    [Fact]
    public async Task AllSinks_ReceiveFrame()
    {
        var (hub, source) = await CapturingHubAsync();
        var first = new RecordingSink();
        var second = new RecordingSink();
        using var _ = hub.AddSink(first);
        using var __ = hub.AddSink(second);

        await source.EmitAsync(Frame(7));

        Assert.Equal([7], first.Frames.Select(f => f.Pcm[0]));
        Assert.Equal([7], second.Frames.Select(f => f.Pcm[0]));
    }

    [Fact]
    public async Task ThrowingSink_DoesNotStarveOthers()
    {
        // 一个 sink 崩掉不该让其余 sink 收不到帧——它们之间没有任何依赖关系。
        var (hub, source) = await CapturingHubAsync();
        using var _ = hub.AddSink(new ThrowingSink());
        var healthy = new RecordingSink();
        using var __ = hub.AddSink(healthy);

        await source.EmitAsync(Frame());

        Assert.Single(healthy.Frames);
    }

    [Fact]
    public async Task ThrowingSink_IsNotAutoRemoved()
    {
        // 瞬时异常不等价于注销。自动摘除会让一次偶发失败变成永久静默，
        // 且 sink 无从得知自己已被摘除。
        var (hub, source) = await CapturingHubAsync();
        var flaky = new ThrowingSink();
        using var _ = hub.AddSink(flaky);

        await source.EmitAsync(Frame());
        await source.EmitAsync(Frame());

        Assert.Equal(2, flaky.Attempts);
        Assert.Equal(1, hub.SinkCount);
    }

    [Fact]
    public async Task DisposedSubscription_StopsReceiving()
    {
        var (hub, source) = await CapturingHubAsync();
        var sink = new RecordingSink();
        var subscription = hub.AddSink(sink);
        Assert.Equal(1, hub.SinkCount);

        subscription.Dispose();
        await source.EmitAsync(Frame());

        Assert.Empty(sink.Frames);
        Assert.Equal(0, hub.SinkCount);
    }

    [Fact]
    public async Task DisposedTwice_IsHarmless()
    {
        var (hub, _) = await CapturingHubAsync();
        var subscription = hub.AddSink(new RecordingSink());

        subscription.Dispose();
        subscription.Dispose();

        Assert.Equal(0, hub.SinkCount);
    }

    [Fact]
    public async Task NoSinks_FrameIsDropped()
    {
        var (hub, source) = await CapturingHubAsync();

        await source.EmitAsync(Frame());

        Assert.Equal(0, hub.SinkCount);
    }

    // ---- 按需启停 ----

    [Fact]
    public async Task Demand_StartsCapture()
    {
        var source = new FakeAudioFrameSource();
        var hub = new AudioFrameHub(source);

        await hub.SetCaptureDemandAsync(true, CancellationToken.None);

        Assert.Equal(1, source.StartCount);
        Assert.True(hub.IsCapturing);
    }

    [Fact]
    public async Task RepeatedDemand_IsIdempotent()
    {
        // 宿主每次会话事件都会重算并调用本方法，重复传相同值必须是 no-op，
        // 否则一次订阅风暴会把音频设备反复开关。
        var source = new FakeAudioFrameSource();
        var hub = new AudioFrameHub(source);

        await hub.SetCaptureDemandAsync(true, CancellationToken.None);
        await hub.SetCaptureDemandAsync(true, CancellationToken.None);
        await hub.SetCaptureDemandAsync(true, CancellationToken.None);

        Assert.Equal(1, source.StartCount);
    }

    [Fact]
    public async Task DemandWithdrawn_StopsCapture()
    {
        var source = new FakeAudioFrameSource();
        var hub = new AudioFrameHub(source);
        await hub.SetCaptureDemandAsync(true, CancellationToken.None);

        await hub.SetCaptureDemandAsync(false, CancellationToken.None);

        Assert.Equal(1, source.StopCount);
        Assert.False(hub.IsCapturing);
    }

    [Fact]
    public async Task StopWithoutPriorStart_DoesNotTouchSource()
    {
        // 不对从未启动的源发停止：宿主启动时会重算一次，此刻需求为假是常态。
        var source = new FakeAudioFrameSource();
        var hub = new AudioFrameHub(source);

        await hub.SetCaptureDemandAsync(false, CancellationToken.None);

        Assert.Equal(0, source.StopCount);
    }

    [Fact]
    public async Task UnavailableSource_DemandIsNoOp()
    {
        // native DLL 缺失时采集不可用，但接收、转发、可视化都是纯托管的，必须照常工作。
        var source = new FakeAudioFrameSource { IsAvailable = false, FailureReason = "缺少 MediaIsland.Audio" };
        var hub = new AudioFrameHub(source);

        await hub.SetCaptureDemandAsync(true, CancellationToken.None);

        Assert.False(hub.IsCapturing);
        Assert.Equal(0, source.StartCount);
    }

    [Fact]
    public async Task StartFailure_DoesNotEscapeAndLeavesNotCapturing()
    {
        // 采集起不来不该崩掉宿主：音频只是可选能力，其余频道必须继续服务。
        var source = new FakeAudioFrameSource { ThrowOnStart = true };
        var hub = new AudioFrameHub(source);

        await hub.SetCaptureDemandAsync(true, CancellationToken.None);

        Assert.False(hub.IsCapturing);
    }

    [Fact]
    public async Task StartFailure_CanBeRetriedByNextDemand()
    {
        // IsCapturing 停在假，故下一次重算仍视为「需要启动」，具备自愈能力。
        var source = new FakeAudioFrameSource { ThrowOnStart = true };
        var hub = new AudioFrameHub(source);
        await hub.SetCaptureDemandAsync(true, CancellationToken.None);

        source.ThrowOnStart = false;
        await hub.SetCaptureDemandAsync(true, CancellationToken.None);

        Assert.True(hub.IsCapturing);
    }

    [Fact]
    public async Task StoppedSource_NoLongerDeliversFrames()
    {
        var source = new FakeAudioFrameSource();
        var hub = new AudioFrameHub(source);
        var sink = new RecordingSink();
        using var _ = hub.AddSink(sink);

        await hub.SetCaptureDemandAsync(true, CancellationToken.None);
        await source.EmitAsync(Frame());
        await hub.SetCaptureDemandAsync(false, CancellationToken.None);
        await source.EmitAsync(Frame());

        // 停止后仍到达的帧（采集线程尚未完全退出）不应投递给 sink。
        Assert.Single(sink.Frames);
    }

    // ---- 夹具 ----

    private sealed class FakeAudioFrameSource : IAudioFrameSource
    {
        public bool IsAvailable { get; init; } = true;

        public string? FailureReason { get; init; }

        public bool ThrowOnStart { get; set; }

        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

        public event Action<AudioFrame>? FrameAvailable;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (ThrowOnStart)
            {
                throw new InvalidOperationException("采集设备被独占");
            }

            StartCount++;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            StopCount++;
            return Task.CompletedTask;
        }

        public Task EmitAsync(AudioFrame frame)
        {
            FrameAvailable?.Invoke(frame);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSink : IAudioFrameSink
    {
        private readonly List<AudioFrame> _frames = [];

        public IReadOnlyList<AudioFrame> Frames
        {
            get { lock (_frames) return _frames.ToArray(); }
        }

        public ValueTask OnFrameAsync(AudioFrame frame, CancellationToken cancellationToken)
        {
            lock (_frames) _frames.Add(frame);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingSink : IAudioFrameSink
    {
        public int Attempts { get; private set; }

        public ValueTask OnFrameAsync(AudioFrame frame, CancellationToken cancellationToken)
        {
            Attempts++;
            throw new InvalidOperationException("sink 内部错误");
        }
    }
}
