using MediaIsland.Services.Audio;
using MediaIsland.Services.MediaLink;
using MediaIsland.Services.MediaLink.Protocol;
using Xunit;

namespace MediaIsland.Tests.MediaLink;

/// <summary>
/// 转发。核心契约是「原样」——下游收到的字节必须与上游发出的逐一相等，
/// 包括三个时间戳与 seq。改写它们会把上游那一跳的时间信息抹掉，
/// 下游算出的传输延迟就只覆盖最后一跳。
///
/// 这一层从宿主服务里抽出来，是因为端到端驱动它需要 MediaSourceCoordinator，
/// 而后者需要 IMediaService 与 LyricsSearchService。为验证十行分支拖进整个
/// 媒体与歌词子系统，测的东西会远多于要测的东西。
/// </summary>
public class MediaLinkAudioForwardingTests
{
    private const string Token = "abc123";

    private sealed class RecordingSubmitter : IAudioFrameSubmitter
    {
        public List<AudioFrame> Frames { get; } = [];

        public void Submit(AudioFrame frame) => Frames.Add(frame);
    }

    private static byte[] BuildFrame(string trackToken, short[] samples, uint seq = 7)
    {
        var pcm = new byte[samples.Length * sizeof(short)];
        Buffer.BlockCopy(samples, 0, pcm, 0, pcm.Length);

        var header = new MediaLinkAudioFrameHeader(
            StartPositionMs: 12_345,
            CapturedAtMs: 1_700_000_000_000,
            ServerTimeMs: 1_700_000_000_020,
            Seq: seq,
            TrackToken: trackToken,
            Flags: MediaLinkAudioFrameFlags.None);

        return MediaLinkAudioFrame.Encode(in header, pcm);
    }

    private static MediaLinkAudioRelay CreateRelay(
        Func<byte[], CancellationToken, Task>? forwarder,
        out RecordingSubmitter submitter)
    {
        submitter = new RecordingSubmitter();
        return new MediaLinkAudioRelay(
            new MediaLinkAudioReceiver(submitter, () => Token),
            forwarder);
    }

    [Fact]
    public async Task AcceptedFrame_IsForwardedByteForByte()
    {
        var forwarded = new List<byte[]>();
        var relay = CreateRelay((bytes, _) => { forwarded.Add(bytes); return Task.CompletedTask; },
            out var submitter);
        var frame = BuildFrame(Token, [1, -1, 2, -2]);

        await relay.HandleAsync(frame);

        // 地基先钉住：确实被接受了，否则下面的断言在「什么都没发生」时也会通过。
        Assert.Single(submitter.Frames);
        var only = Assert.Single(forwarded);
        Assert.Same(frame, only);   // 原样即同一个数组，没有中间拷贝
        Assert.Equal(1, relay.ForwardedFrames);
    }

    [Fact]
    public async Task MismatchedToken_IsNotForwarded()
    {
        var forwarded = new List<byte[]>();
        var relay = CreateRelay((bytes, _) => { forwarded.Add(bytes); return Task.CompletedTask; },
            out var submitter);

        await relay.HandleAsync(BuildFrame("otherToken", [1, -1]));

        Assert.Empty(forwarded);
        Assert.Empty(submitter.Frames);
        Assert.Equal(0, relay.ForwardedFrames);
    }

    [Fact]
    public async Task MalformedFrame_IsNotForwarded()
    {
        var forwarded = new List<byte[]>();
        var relay = CreateRelay((bytes, _) => { forwarded.Add(bytes); return Task.CompletedTask; },
            out var submitter);

        await relay.HandleAsync([0xFF, 0xFF, 0x01, 0x00, 0x00]);

        Assert.Empty(forwarded);
        // 与同类用例对齐：单看「没转发」，在「什么都没发生」时也成立。
        Assert.Empty(submitter.Frames);
    }

    [Fact]
    public async Task EmptyPcmFrame_IsStillForwarded()
    {
        // 合法但无内容的帧仍要转发：下游可能用它推进时间轴。
        // Handle 对空 PCM 返回 None 而不计入 AcceptedFrames，转发判据看的是返回值。
        var forwarded = new List<byte[]>();
        var relay = CreateRelay((bytes, _) => { forwarded.Add(bytes); return Task.CompletedTask; }, out _);

        await relay.HandleAsync(BuildFrame(Token, []));

        Assert.Single(forwarded);
    }

    [Fact]
    public async Task ForwarderThrowing_DoesNotPropagate()
    {
        // 转发失败是下游连接的问题，不该让上游的收循环停摆——
        // 那会把一次下游故障升级成本机全部音频功能失效。
        var relay = CreateRelay((_, _) => throw new IOException("下游断了"), out var submitter);

        await relay.HandleAsync(BuildFrame(Token, [1, -1]));
        await relay.HandleAsync(BuildFrame(Token, [2, -2]));

        Assert.Equal(2, submitter.Frames.Count);   // 本机消费不受影响
        Assert.Equal(0, relay.ForwardedFrames);    // 抛了就不算成功转发
    }

    [Fact]
    public async Task NoForwarder_IsNotAnError()
    {
        // 只做接收不做服务端的实例没有转发目标，这是正常配置而非故障。
        var relay = CreateRelay(forwarder: null, out var submitter);

        await relay.HandleAsync(BuildFrame(Token, [1, -1]));

        Assert.Single(submitter.Frames);
        Assert.Equal(0, relay.ForwardedFrames);
    }

    [Fact]
    public async Task ReceiverThrowing_DoesNotPropagate()
    {
        // 单帧处理失败不该拖垮收循环——那会把一次数据问题升级成一次断连。
        var relay = new MediaLinkAudioRelay(
            new MediaLinkAudioReceiver(new ThrowingSubmitter(), () => Token),
            (_, _) => Task.CompletedTask);

        await relay.HandleAsync(BuildFrame(Token, [1, -1]));

        Assert.Equal(0, relay.ForwardedFrames);
    }

    private sealed class ThrowingSubmitter : IAudioFrameSubmitter
    {
        public void Submit(AudioFrame frame) => throw new InvalidOperationException("下游汇炸了");
    }
}
