using MediaIsland.Services.Audio;
using MediaIsland.Services.MediaLink;
using MediaIsland.Services.MediaLink.Protocol;
using Xunit;

namespace MediaIsland.Tests.MediaLink;

/// <summary>
/// 接收侧接缝。测试全部走真实的 <see cref="MediaLinkAudioFrame.Encode"/> 造帧，
/// 不手搓字节：手搓的帧只能证明「解码器接受我造的东西」，而真正要证明的是
/// 「解码器接受编码器产出的东西」——那才是跨实例通信时线上真实出现的字节。
///
/// 三类拒绝的语义要分清：Malformed 是帧本身坏了（可能来自未来版本或半个帧），
/// OddPcmLength 是帧结构对但载荷不可解释，TrackMismatch 是帧完全正确但已经过期。
/// 三者都丢帧不断连——它们是数据问题，不是连接问题，断连只会让用户看到一次无谓的重连。
/// </summary>
public class MediaLinkAudioReceiverTests
{
    private const string Token = "track-abc";
    private const long UpstreamCapturedAtMs = 1_700_000_000_000L;

    /// <summary>本地 QPC 读数。刻意取一个与上游时间戳量级完全不同的值，便于断言没搞混。</summary>
    private const long LocalQpc100Ns = 987_654_321L;

    private readonly RecordingSubmitter _submitter = new();
    private string? _currentToken = Token;

    private MediaLinkAudioReceiver NewReceiver() =>
        new(_submitter, () => _currentToken, nowQpc100NsProvider: () => LocalQpc100Ns);

    private static MediaLinkAudioFrameHeader Header(
        string token, MediaLinkAudioFrameFlags flags = MediaLinkAudioFrameFlags.None) =>
        new(
            StartPositionMs: 5000,
            CapturedAtMs: UpstreamCapturedAtMs,
            ServerTimeMs: UpstreamCapturedAtMs + 3,
            Seq: 42,
            TrackToken: token,
            Flags: flags);

    private static byte[] Encode(
        string token, byte[] pcm, MediaLinkAudioFrameFlags flags = MediaLinkAudioFrameFlags.None)
    {
        var header = Header(token, flags);
        return MediaLinkAudioFrame.Encode(in header, pcm);
    }

    /// <summary>可辨识的 PCM 模式。步长取质数，与 2/4 字节边界都不对齐，偏移错误会立刻显形。</summary>
    private static byte[] Pcm(int byteCount)
    {
        var pcm = new byte[byteCount];
        for (var i = 0; i < pcm.Length; i++)
        {
            pcm[i] = (byte)(i * 7 % 251);
        }

        return pcm;
    }

    // ---- 正常路径 ----

    [Fact]
    public void ValidFrame_IsAcceptedAndSubmitted()
    {
        var receiver = NewReceiver();
        var pcm = Pcm(1920);

        var reason = receiver.Handle(Encode(Token, pcm));

        Assert.Equal(MediaLinkAudioRejectReason.None, reason);
        Assert.Equal(1, receiver.AcceptedFrames);
        Assert.Equal(0, receiver.RejectedFrames);
        Assert.Single(_submitter.Frames);
        Assert.Equal(pcm.Length, _submitter.Frames[0].Pcm.Length);
    }

    [Fact]
    public void SubmittedFrame_CarriesProtocolFormatAndExactPcm()
    {
        var receiver = NewReceiver();
        var pcm = Pcm(1920);

        receiver.Handle(Encode(Token, pcm));

        var submitted = _submitter.Frames[0];
        // 格式取协议常量而非帧头——线格式已冻结为 48kHz/2ch/i16，帧头里根本没有这两个字段。
        Assert.Equal(MediaLinkProtocol.AudioSampleRate, submitted.SampleRate);
        Assert.Equal(MediaLinkProtocol.AudioChannels, submitted.Channels);
        // 逐字节相等：任何偏移错误（比如把 trackToken 的尾巴算进 PCM）都会在这里显形。
        Assert.Equal(pcm, submitted.Pcm);
    }

    [Fact]
    public void SubmittedFrame_UsesLocalClockNotUpstreamTimestamp()
    {
        var receiver = NewReceiver();

        receiver.Handle(Encode(Token, Pcm(64)));

        var submitted = _submitter.Frames[0];
        // 帧头的 capturedAtMs 是上游机器的墙钟，跨机没有可比性；而下游只用 QPC 判断
        // 帧的新鲜度，那是本地语义。接收侧的「采样时刻」本就是本机对这块 PCM 的第一次观测。
        Assert.Equal(LocalQpc100Ns, submitted.QpcPosition100Ns);
        Assert.NotEqual(UpstreamCapturedAtMs, submitted.QpcPosition100Ns);
    }

    [Fact]
    public void SilentFlag_PropagatesToTheFrame()
    {
        var receiver = NewReceiver();

        receiver.Handle(Encode(Token, Pcm(64), MediaLinkAudioFrameFlags.Silent));

        // 静音帧仍携带完整长度的零值 PCM 并照常提交：时间轴必须推进，
        // 否则可视化会冻结在最后一帧波形上而不是归零。
        Assert.True(_submitter.Frames[0].IsSilent);
    }

    [Fact]
    public void NonSilentFrame_IsNotMarkedSilent()
    {
        var receiver = NewReceiver();

        // TrackStart 也置位，确认标志位是按位判断而非整值相等。
        receiver.Handle(Encode(Token, Pcm(64), MediaLinkAudioFrameFlags.TrackStart));

        Assert.False(_submitter.Frames[0].IsSilent);
    }

    [Fact]
    public void LastHeader_ReflectsTheAcceptedFrame()
    {
        var receiver = NewReceiver();

        receiver.Handle(Encode(Token, Pcm(64), MediaLinkAudioFrameFlags.TrackStart));

        Assert.Equal(Header(Token, MediaLinkAudioFrameFlags.TrackStart), receiver.LastHeader);
    }

    // ---- 帧本身坏了 ----

    [Fact]
    public void BadMagic_IsRejectedAsMalformed()
    {
        var receiver = NewReceiver();
        var frame = Encode(Token, Pcm(64));
        frame[0] = 0x00;

        var reason = receiver.Handle(frame);

        // 丢帧不断连：未知 magic 可能是未来版本的其他二进制帧类型，
        // 协议要求必须容忍未知而不是把连接拆掉。
        Assert.Equal(MediaLinkAudioRejectReason.Malformed, reason);
        Assert.Equal(1, receiver.RejectedFrames);
        Assert.Equal(0, receiver.AcceptedFrames);
        Assert.Empty(_submitter.Frames);
    }

    [Fact]
    public void UnsupportedVersion_IsRejectedAsMalformed()
    {
        var receiver = NewReceiver();
        var frame = Encode(Token, Pcm(64));
        frame[2] = 2;

        Assert.Equal(MediaLinkAudioRejectReason.Malformed, receiver.Handle(frame));
        Assert.Empty(_submitter.Frames);
    }

    [Fact]
    public void FrameShorterThanTheFixedHeader_IsRejectedAsMalformed()
    {
        var receiver = NewReceiver();

        // 少一个字节就不该被受理：定长头部按固定偏移读，短一字节会读到越界或垃圾。
        var truncated = new byte[MediaLinkAudioFrame.FixedHeaderBytes - 1];

        Assert.Equal(MediaLinkAudioRejectReason.Malformed, receiver.Handle(truncated));
        Assert.Empty(_submitter.Frames);
    }

    [Fact]
    public void FrameTruncatedInsideTheToken_IsRejectedAsMalformed()
    {
        var receiver = NewReceiver();
        var frame = Encode(Token, Pcm(64));

        // 头部完整但 token 被截断——比「整帧太短」更隐蔽，长度字段说有 N 字节而实际没有。
        var truncated = frame[..(MediaLinkAudioFrame.FixedHeaderBytes + 1)];

        Assert.Equal(MediaLinkAudioRejectReason.Malformed, receiver.Handle(truncated));
        Assert.Empty(_submitter.Frames);
    }

    // ---- 载荷不可解释 ----

    [Fact]
    public void OddPcmLength_IsRejected()
    {
        var receiver = NewReceiver();

        // 第 1 期挂账「PCM 奇数字节留给消费侧」——这里就是那个消费侧。
        // i16 要求偶数字节，奇数说明上游编码有误，继续算会让整段声道错位半个样本。
        var reason = receiver.Handle(Encode(Token, Pcm(1921)));

        Assert.Equal(MediaLinkAudioRejectReason.OddPcmLength, reason);
        Assert.Equal(1, receiver.RejectedFrames);
        Assert.Empty(_submitter.Frames);
    }

    // ---- 帧对但过期 ----

    [Fact]
    public void TrackTokenMismatch_IsRejected()
    {
        var receiver = NewReceiver();
        _currentToken = "another-track";

        var reason = receiver.Handle(Encode(Token, Pcm(64)));

        // 与歌词同源的既有规则：切歌后过期曲目的 PCM 直接丢弃，
        // 否则会听到上一首的频谱跟着新歌跳。
        Assert.Equal(MediaLinkAudioRejectReason.TrackMismatch, reason);
        Assert.Empty(_submitter.Frames);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoCurrentTrack_IsRejected(string? current)
    {
        var receiver = NewReceiver();
        _currentToken = current;

        // 无从校验即不接受。空串与 null 同等对待：两者都表示「本机还不知道在放什么」。
        Assert.Equal(MediaLinkAudioRejectReason.TrackMismatch, receiver.Handle(Encode(Token, Pcm(64))));
        Assert.Empty(_submitter.Frames);
    }

    [Fact]
    public void TokenComparison_IsOrdinalAndCaseSensitive()
    {
        var receiver = NewReceiver();
        _currentToken = Token.ToUpperInvariant();

        // token 是哈希串，大小写不同就是不同的曲目。用忽略大小写的比较会让
        // 两个不同曲目偶然互相接受。
        Assert.Equal(MediaLinkAudioRejectReason.TrackMismatch, receiver.Handle(Encode(Token, Pcm(64))));
    }

    // ---- 空载荷 ----

    [Fact]
    public void EmptyPcm_IsAcceptedButNotSubmitted()
    {
        var receiver = NewReceiver();

        var reason = receiver.Handle(Encode(Token, []));

        // 帧本身合法，只是没有内容可分析。不算拒绝（上游没做错），也不提交（提交等于
        // 让分析器处理一个零长缓冲）。
        Assert.Equal(MediaLinkAudioRejectReason.None, reason);
        Assert.Equal(0, receiver.AcceptedFrames);
        Assert.Equal(0, receiver.RejectedFrames);
        Assert.Empty(_submitter.Frames);
    }

    // ---- 计数 ----

    [Fact]
    public void Counters_AccumulateAcrossFrames()
    {
        var receiver = NewReceiver();
        var bad = Encode(Token, Pcm(64));
        bad[0] = 0x00;

        receiver.Handle(Encode(Token, Pcm(64)));
        receiver.Handle(Encode(Token, Pcm(64)));
        receiver.Handle(bad);
        receiver.Handle(Encode(Token, Pcm(65)));

        // 计数用于诊断「上游在发但我们全丢了」这类问题，必须分别累加而不是互相覆盖。
        Assert.Equal(2, receiver.AcceptedFrames);
        Assert.Equal(2, receiver.RejectedFrames);
        Assert.Equal(2, _submitter.Frames.Count);
    }

    [Fact]
    public void NullDependencies_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => new MediaLinkAudioReceiver(null!, () => Token));
        Assert.Throws<ArgumentNullException>(() => new MediaLinkAudioReceiver(_submitter, null!));
    }

    private sealed class RecordingSubmitter : IAudioFrameSubmitter
    {
        public List<AudioFrame> Frames { get; } = [];

        public void Submit(AudioFrame frame) => Frames.Add(frame);
    }
}
