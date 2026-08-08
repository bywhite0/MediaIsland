using MediaIsland.Services.Audio;
using MediaIsland.Services.Media;
using MediaIsland.Services.MediaLink;
using MediaIsland.Services.MediaLink.Mapping;
using MediaIsland.Services.MediaLink.Protocol;
using Xunit;

namespace MediaIsland.Tests.MediaLink;

/// <summary>
/// 采集帧到 MediaLink 音频帧的转换与广播。
///
/// 广播器是 Services/Audio 与 Services/MediaLink 的**唯一接缝**：它知道曲目
/// （问 MediaSourceCoordinator）也知道协议（调 MediaLinkAudioFrame.Encode），
/// 而两边都不知道对方。
///
/// 这里锁定的最关键一条是 trackToken 与 media.updated **同源**——不同源会让客户端
/// 按协议规则比对后丢弃全部音频，且失败是完全静默的。
/// </summary>
public class MediaLinkAudioBroadcasterTests
{
    private const int SampleRate = MediaLinkProtocol.AudioSampleRate;
    private const int Channels = MediaLinkProtocol.AudioChannels;

    /// <summary>20ms @ 48kHz 立体声 i16 = 3840 字节，即协议约定的一帧。</summary>
    private static byte[] Pcm20Ms() => new byte[SampleRate / 50 * Channels * sizeof(short)];

    private static AudioFrame Frame(long qpc100Ns = 0, bool silent = false, byte[]? pcm = null) =>
        new(pcm ?? Pcm20Ms(), qpc100Ns, SampleRate, Channels, silent);

    private static MediaInfo Media(
        string title = "曲目",
        MediaPlaybackState state = MediaPlaybackState.Playing,
        double positionMs = 30_000) =>
        new(
            SourceApp: "Spotify.exe",
            Title: title,
            Artist: "艺人",
            AlbumTitle: "专辑",
            Position: TimeSpan.FromMilliseconds(positionMs),
            Duration: TimeSpan.FromMinutes(4),
            PlaybackInfo: new MediaPlaybackInfo(state),
            Thumbnail: null,
            ThumbnailSource: null);

    private sealed class Harness
    {
        public MediaLinkSessionHub Hub { get; } = new();
        public MediaInfo? Media { get; set; }
        public long NowUnixMs { get; set; } = 1_710_000_000_000;
        public long NowQpc100Ns { get; set; } = 10_000_000;

        public MediaLinkAudioBroadcaster Create() =>
            new(Hub, () => Media, () => NowUnixMs, () => NowQpc100Ns);
    }

    private static async Task<(MediaLinkSession Session, FakeMediaLinkSocket Socket)> SubscribedSessionAsync(
        MediaLinkSessionHub hub)
    {
        var socket = new FakeMediaLinkSocket();
        var session = new MediaLinkSession(socket, new MediaLinkSessionOptions { ExpectedToken = "t" });
        session.StartWriter(CancellationToken.None);
        _ = session.RunAsync(CancellationToken.None);

        socket.EnqueueIncoming("""{"type":"auth","id":"a1","v":1,"ts":0,"payload":{"token":"t"}}""");
        await WaitUntilAsync(() => session.IsAuthenticated);
        socket.EnqueueIncoming(
            """{"type":"subscribe","id":"s1","v":1,"ts":0,"payload":{"channels":["media","audio"]}}""");
        await WaitUntilAsync(() => session.IsSubscribedToAudio);
        socket.ClearOutgoing();

        hub.Add(session);
        return (session, socket);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        throw new TimeoutException("条件未在 5 秒内满足");
    }

    /// <summary>
    /// 解码并把 PCM 拷成数组。必须是同步方法：ReadOnlySpan 是 ref struct，
    /// C# 12 不允许它出现在 async 方法体内（CS9202）——这与 MediaLinkSession.TryDecodeCopy
    /// 面对的是同一条约束。
    /// </summary>
    private static (MediaLinkAudioFrameHeader Header, byte[] Pcm) Decode(byte[] encoded)
    {
        Assert.True(MediaLinkAudioFrame.TryDecode(encoded, out var header, out var pcm, out var error));
        Assert.Equal(MediaLinkAudioFrameDecodeError.None, error);
        return (header, pcm.ToArray());
    }

    private static async Task<MediaLinkAudioFrameHeader> SendAndDecodeAsync(
        MediaLinkAudioBroadcaster broadcaster,
        FakeMediaLinkSocket socket,
        AudioFrame frame)
    {
        var before = socket.OutgoingBinary.Count;
        await broadcaster.OnFrameAsync(frame, CancellationToken.None);
        await WaitUntilAsync(() => socket.OutgoingBinary.Count > before);

        return Decode(socket.OutgoingBinary.Last()).Header;
    }

    // ---- trackToken 同源（最关键）----

    [Fact]
    public async Task TrackToken_MatchesMediaUpdatedToken()
    {
        // 不同源 → 客户端按协议规则丢弃全部音频，且完全静默。这条必须逐字相同。
        var harness = new Harness { Media = Media() };
        var (_, socket) = await SubscribedSessionAsync(harness.Hub);
        var broadcaster = harness.Create();

        var header = await SendAndDecodeAsync(broadcaster, socket, Frame());

        var expected = MediaLinkDtoMapper.ToMediaDto(harness.Media, MediaInfoChangeKind.CurrentSession)!.TrackToken;
        Assert.Equal(expected, header.TrackToken);
    }

    [Fact]
    public async Task NoCurrentTrack_SendsNothing()
    {
        // 没有曲目就没有时间轴，发出去的帧对不上任何东西。
        var harness = new Harness { Media = null };
        var (_, socket) = await SubscribedSessionAsync(harness.Hub);
        var broadcaster = harness.Create();

        await broadcaster.OnFrameAsync(Frame(), CancellationToken.None);

        await Task.Delay(150);
        Assert.Empty(socket.OutgoingBinary);
    }

    // ---- seq ----

    [Fact]
    public async Task Seq_IncrementsPerFrame()
    {
        var harness = new Harness { Media = Media() };
        var (_, socket) = await SubscribedSessionAsync(harness.Hub);
        var broadcaster = harness.Create();

        var first = await SendAndDecodeAsync(broadcaster, socket, Frame());
        var second = await SendAndDecodeAsync(broadcaster, socket, Frame());
        var third = await SendAndDecodeAsync(broadcaster, socket, Frame());

        Assert.Equal(first.Seq + 1, second.Seq);
        Assert.Equal(second.Seq + 1, third.Seq);
    }

    // ---- TrackStart 标志 ----

    [Fact]
    public async Task FirstFrame_SetsTrackStart()
    {
        var harness = new Harness { Media = Media() };
        var (_, socket) = await SubscribedSessionAsync(harness.Hub);
        var broadcaster = harness.Create();

        var header = await SendAndDecodeAsync(broadcaster, socket, Frame());

        Assert.True(header.Flags.HasFlag(MediaLinkAudioFrameFlags.TrackStart));
    }

    [Fact]
    public async Task SubsequentFramesOfSameTrack_DoNotSetTrackStart()
    {
        var harness = new Harness { Media = Media() };
        var (_, socket) = await SubscribedSessionAsync(harness.Hub);
        var broadcaster = harness.Create();

        await SendAndDecodeAsync(broadcaster, socket, Frame());
        var second = await SendAndDecodeAsync(broadcaster, socket, Frame());

        Assert.False(second.Flags.HasFlag(MediaLinkAudioFrameFlags.TrackStart));
    }

    [Fact]
    public async Task TrackChange_SetsTrackStartAgain()
    {
        var harness = new Harness { Media = Media("第一首") };
        var (_, socket) = await SubscribedSessionAsync(harness.Hub);
        var broadcaster = harness.Create();
        await SendAndDecodeAsync(broadcaster, socket, Frame());

        harness.Media = Media("第二首");
        var afterSwitch = await SendAndDecodeAsync(broadcaster, socket, Frame());

        Assert.True(afterSwitch.Flags.HasFlag(MediaLinkAudioFrameFlags.TrackStart));
        var expected = MediaLinkDtoMapper.ToMediaDto(harness.Media, MediaInfoChangeKind.CurrentSession)!.TrackToken;
        Assert.Equal(expected, afterSwitch.TrackToken);
    }

    // ---- Silent 标志 ----

    [Fact]
    public async Task SilentFrame_SetsSilentFlagAndKeepsFullLengthPcm()
    {
        // 静音帧仍要携带完整长度的 PCM，接收端才能推进时间轴而非冻结。
        var harness = new Harness { Media = Media() };
        var (_, socket) = await SubscribedSessionAsync(harness.Hub);
        var broadcaster = harness.Create();

        var frame = Frame(silent: true);
        await broadcaster.OnFrameAsync(frame, CancellationToken.None);
        await WaitUntilAsync(() => socket.OutgoingBinary.Count == 1);

        var (header, pcm) = Decode(socket.OutgoingBinary.First());
        Assert.True(header.Flags.HasFlag(MediaLinkAudioFrameFlags.Silent));
        Assert.Equal(3840, pcm.Length);
    }

    // ---- startPositionMs 推算 ----

    [Fact]
    public async Task Playing_StartPositionRewindsByFrameAge()
    {
        // 位置是「现在」的读数，而帧采于 ageMs 之前，故要往回拨。
        var harness = new Harness { Media = Media(positionMs: 30_000), NowQpc100Ns = 10_000_000 };
        var (_, socket) = await SubscribedSessionAsync(harness.Hub);
        var broadcaster = harness.Create();

        // 帧的 QPC 比"现在"早 100ms（100ms = 1_000_000 个 100ns 单位）
        var header = await SendAndDecodeAsync(broadcaster, socket, Frame(qpc100Ns: 10_000_000 - 1_000_000));

        Assert.Equal(29_900, header.StartPositionMs);
    }

    [Fact]
    public async Task Paused_DoesNotExtrapolatePosition()
    {
        // 暂停时曲目位置不随时间前进，回拨会得到一个曲目上不存在的位置。
        var harness = new Harness
        {
            Media = Media(state: MediaPlaybackState.Paused, positionMs: 30_000),
            NowQpc100Ns = 10_000_000
        };
        var (_, socket) = await SubscribedSessionAsync(harness.Hub);
        var broadcaster = harness.Create();

        var header = await SendAndDecodeAsync(broadcaster, socket, Frame(qpc100Ns: 10_000_000 - 1_000_000));

        Assert.Equal(30_000, header.StartPositionMs);
    }

    [Fact]
    public async Task StartPosition_NeverNegative()
    {
        // 曲目刚开始时帧龄可能超过已播放时长，负位置对接收端没有意义。
        var harness = new Harness { Media = Media(positionMs: 10), NowQpc100Ns = 10_000_000 };
        var (_, socket) = await SubscribedSessionAsync(harness.Hub);
        var broadcaster = harness.Create();

        var header = await SendAndDecodeAsync(broadcaster, socket, Frame(qpc100Ns: 10_000_000 - 5_000_000));

        Assert.Equal(0, header.StartPositionMs);
    }

    // ---- capturedAtMs / serverTimeMs ----

    [Fact]
    public async Task CapturedAtPrecedesServerTime_ByFrameAge()
    {
        // 两者是同一时钟内求差的一对值，差值即本机侧的采集到发送耗时。
        var harness = new Harness { NowUnixMs = 1_710_000_000_000, NowQpc100Ns = 10_000_000, Media = Media() };
        var (_, socket) = await SubscribedSessionAsync(harness.Hub);
        var broadcaster = harness.Create();

        var header = await SendAndDecodeAsync(broadcaster, socket, Frame(qpc100Ns: 10_000_000 - 200_000));

        Assert.Equal(1_710_000_000_000, header.ServerTimeMs);
        Assert.Equal(1_710_000_000_000 - 20, header.CapturedAtMs);
    }

    // ---- 往返自洽 ----

    [Fact]
    public async Task EncodedFrame_RoundTripsThroughDecoder()
    {
        var harness = new Harness { Media = Media() };
        var (_, socket) = await SubscribedSessionAsync(harness.Hub);
        var broadcaster = harness.Create();

        var pcm = new byte[3840];
        pcm[0] = 0xAB;
        pcm[3839] = 0xCD;
        var frame = Frame(pcm: pcm);
        await broadcaster.OnFrameAsync(frame, CancellationToken.None);
        await WaitUntilAsync(() => socket.OutgoingBinary.Count == 1);

        var (_, decoded) = Decode(socket.OutgoingBinary.First());
        Assert.Equal(0xAB, decoded[0]);
        Assert.Equal(0xCD, decoded[3839]);
    }

    // ---- 订阅门禁 ----

    [Fact]
    public async Task NonAudioSubscriber_ReceivesNothing()
    {
        var harness = new Harness { Media = Media() };
        var socket = new FakeMediaLinkSocket();
        var session = new MediaLinkSession(socket, new MediaLinkSessionOptions { ExpectedToken = "t" });
        session.StartWriter(CancellationToken.None);
        _ = session.RunAsync(CancellationToken.None);
        socket.EnqueueIncoming("""{"type":"auth","id":"a1","v":1,"ts":0,"payload":{"token":"t"}}""");
        await WaitUntilAsync(() => session.IsAuthenticated);
        socket.EnqueueIncoming(
            """{"type":"subscribe","id":"s1","v":1,"ts":0,"payload":{"channels":["media","lyrics"]}}""");
        await WaitUntilAsync(() => socket.Outgoing.Any(o => o.Contains("subscribe_ok")));
        harness.Hub.Add(session);
        var broadcaster = harness.Create();

        await broadcaster.OnFrameAsync(Frame(), CancellationToken.None);

        await Task.Delay(150);
        Assert.Empty(socket.OutgoingBinary);
    }
}
