using MediaIsland.Services.Audio;
using Xunit;

namespace MediaIsland.Tests.Audio;

/// <summary>
/// 本机采集的归属从服务端挪到这里。要锁的是三件事：需求公式原样生效、
/// 可视化 sink 常驻而广播器可挂可摘、释放时真的放掉设备。
/// </summary>
public class LocalAudioCaptureTests
{
    private static AudioFrame OneFrame() => new(new byte[8], 0, 48000, 2, false);

    [Fact]
    public async Task VisualizationDemand_StartsTheSource()
    {
        var source = new FakeAudioFrameSource();
        using var capture = new LocalAudioCapture(source);

        await capture.SetDemandAsync(protocolDemand: false, visualizationDemand: true, externalMediaEffective: false);

        Assert.Equal(1, source.StartCount);
        Assert.True(capture.IsCapturing);
    }

    [Fact]
    public async Task DemandWithdrawn_StopsTheSource()
    {
        var source = new FakeAudioFrameSource();
        using var capture = new LocalAudioCapture(source);
        await capture.SetDemandAsync(false, true, false);

        await capture.SetDemandAsync(false, false, false);

        Assert.Equal(1, source.StopCount);
        Assert.False(capture.IsCapturing);
    }

    [Fact]
    public async Task ExternalMediaEffective_SuppressesCapture()
    {
        var source = new FakeAudioFrameSource();
        using var capture = new LocalAudioCapture(source);

        await capture.SetDemandAsync(true, true, externalMediaEffective: true);

        Assert.Equal(0, source.StartCount);
    }

    [Fact]
    public async Task VisualizationSink_ReceivesFrames_WithoutAnyAttachedSink()
    {
        var source = new FakeAudioFrameSource();
        var visualization = new RecordingSink();
        using var capture = new LocalAudioCapture(source, visualization);
        await capture.SetDemandAsync(false, true, false);

        await source.EmitAsync(OneFrame());

        Assert.Single(visualization.Frames);
    }

    [Fact]
    public async Task AttachedSink_StopsReceivingAfterDispose_WhileVisualizationKeepsReceiving()
    {
        var source = new FakeAudioFrameSource();
        var visualization = new RecordingSink();
        var broadcaster = new RecordingSink();
        using var capture = new LocalAudioCapture(source, visualization);
        await capture.SetDemandAsync(true, true, false);

        var attachment = capture.AttachSink(broadcaster);
        await source.EmitAsync(OneFrame());
        attachment.Dispose();
        await source.EmitAsync(OneFrame());

        Assert.Single(broadcaster.Frames);
        Assert.Equal(2, visualization.Frames.Count);
    }

    [Fact]
    public async Task Dispose_StopsCaptureAndReleasesTheSource()
    {
        var source = new FakeAudioFrameSource();
        var capture = new LocalAudioCapture(source);
        await capture.SetDemandAsync(false, true, false);

        capture.Dispose();

        Assert.Equal(1, source.StopCount);
        Assert.True(source.IsDisposed);
    }

    [Fact]
    public async Task Restart_WhileCapturing_StopsThenStarts()
    {
        var source = new FakeAudioFrameSource();
        using var capture = new LocalAudioCapture(source);
        await capture.SetDemandAsync(false, true, false);

        await capture.RestartAsync();

        Assert.Equal(1, source.StopCount);
        Assert.Equal(2, source.StartCount);
    }

    [Fact]
    public void UnavailableSource_IsReportedWithItsReason()
    {
        var source = new FakeAudioFrameSource { IsAvailable = false, FailureReason = "缺少 MediaIsland.Audio" };
        using var capture = new LocalAudioCapture(source);

        Assert.False(capture.IsSourceAvailable);
        Assert.Equal("缺少 MediaIsland.Audio", capture.SourceFailureReason);
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
}
