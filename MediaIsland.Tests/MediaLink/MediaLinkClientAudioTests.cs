using System.Text.Json;
using MediaIsland.Services.MediaLink;
using MediaIsland.Services.MediaLink.Protocol;
using Xunit;

namespace MediaIsland.Tests.MediaLink;

/// <summary>
/// 客户端消费音频的两件事：收得到二进制帧，以及只在对端支持时才订阅 audio。
///
/// 后者的代价不对称，这是本类最该守住的一条：subscribe 是**整体替换**语义，
/// 向不支持 audio 的老服务端请求它，media 与 lyrics 会**一起被拒**——
/// 用户为了看频谱，连歌词都没了。所以能力判断错在保守一侧只是没频谱，
/// 错在激进一侧是整条链路失效。
/// </summary>
public class MediaLinkClientAudioTests
{
    private const string AudioCapable = ""","capabilities":["audio"]""";
    private const string LegacyServer = "";

    private const string HelloPrefix = """
        {"type":"event","name":"server.hello","v":1,"ts":0,"payload":{"protocolVersion":1,"authRequired":true,"sessionEpoch":
        """;

    private static string BuildHello(long epoch, string capabilitiesJson) =>
        HelloPrefix + epoch + capabilitiesJson + "}}";

    /// <summary>造一个结构合法的音频帧。内容用得上真实编码器，避免测出「只要是字节就转发」。</summary>
    private static byte[] AudioFrame(string token = "track-1", int pcmBytes = 64)
    {
        var header = new MediaLinkAudioFrameHeader(
            StartPositionMs: 1000,
            CapturedAtMs: 1_700_000_000_000L,
            ServerTimeMs: 1_700_000_000_003L,
            Seq: 7,
            TrackToken: token,
            Flags: MediaLinkAudioFrameFlags.None);

        var pcm = new byte[pcmBytes];
        for (var i = 0; i < pcm.Length; i++)
        {
            pcm[i] = (byte)(i * 7 % 251);
        }

        return MediaLinkAudioFrame.Encode(in header, pcm);
    }

    private static (ScriptedClientSocket Socket, MediaLinkClient Client) NewPair(string capabilitiesJson)
    {
        var socket = new ScriptedClientSocket();
        socket.QueueRaw(BuildHello(epoch: 1, capabilitiesJson));
        socket.QueueMessage(MediaLinkProtocol.TypeAuthOk);
        socket.QueueMessage(MediaLinkProtocol.TypeSubscribeOk);

        var client = new MediaLinkClient(new MediaLinkClientOptions
        {
            Endpoint = new Uri("ws://127.0.0.1:1/v1/ws"),
            Token = "tok",
            InitialRetryDelay = TimeSpan.FromMilliseconds(50),
            MaxRetryDelay = TimeSpan.FromMilliseconds(200),
            SocketFactory = () => socket
        });

        return (socket, client);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }

    /// <summary>取出已发送的 subscribe 消息里的 channels 列表。</summary>
    private static IReadOnlyList<string> SubscribedChannels(ScriptedClientSocket socket)
    {
        var subscribe = socket.Sent.Last(s => s.Contains($"\"{MediaLinkProtocol.TypeSubscribe}\""));
        using var document = JsonDocument.Parse(subscribe);
        return document.RootElement
            .GetProperty("payload").GetProperty("channels")
            .EnumerateArray().Select(e => e.GetString()!).ToArray();
    }

    private static bool Sent(ScriptedClientSocket socket, string type) =>
        socket.Sent.Any(s => s.Contains($"\"{type}\""));

    // ---- 按能力裁剪订阅 ----

    [Fact]
    public async Task CapableServerAndAudioWanted_SubscribesAudioAndStartsPlayback()
    {
        var (socket, client) = NewPair(AudioCapable);
        await using var _ = client;

        await client.SetAudioSubscribedAsync(true);
        client.Start();
        await socket.WaitForSendsAsync(3);

        Assert.Contains(MediaLinkProtocol.ChannelAudio, SubscribedChannels(socket));
        Assert.True(Sent(socket, MediaLinkProtocol.TypeAudioPlayStart));
    }

    [Fact]
    public async Task LegacyServerAndAudioWanted_LeavesAudioOutOfSubscription()
    {
        var (socket, client) = NewPair(LegacyServer);
        await using var _ = client;

        await client.SetAudioSubscribedAsync(true);
        client.Start();
        await socket.WaitForSendsAsync(2);

        // 关键的不对称：向不支持的服务端请求 audio，会让 media 与 lyrics 一起被拒。
        // 宁可没有频谱，也不能把歌词一起搭进去。
        var channels = SubscribedChannels(socket);
        Assert.DoesNotContain(MediaLinkProtocol.ChannelAudio, channels);
        Assert.Equal([MediaLinkProtocol.ChannelMedia, MediaLinkProtocol.ChannelLyrics], channels);
        Assert.False(Sent(socket, MediaLinkProtocol.TypeAudioPlayStart));
    }

    [Fact]
    public async Task AudioNotWanted_SubscribesTheDefaultChannelsOnly()
    {
        var (socket, client) = NewPair(AudioCapable);
        await using var _ = client;

        client.Start();
        await socket.WaitForSendsAsync(2);

        // 服务端支持不等于要订：无消费者时订阅 audio 会让上游白采集、白传约 192KB/s。
        Assert.Equal(
            [MediaLinkProtocol.ChannelMedia, MediaLinkProtocol.ChannelLyrics],
            SubscribedChannels(socket));
        Assert.False(Sent(socket, MediaLinkProtocol.TypeAudioPlayStart));
    }

    // ---- 收二进制帧 ----

    [Fact]
    public async Task BinaryFrame_RaisesAudioFrameReceivedWithExactBytes()
    {
        var (socket, client) = NewPair(AudioCapable);
        await using var _ = client;

        var received = new List<byte[]>();
        client.AudioFrameReceived += (_, e) => received.Add(e.Frame);

        client.Start();
        await socket.WaitForSendsAsync(2);

        var frame = AudioFrame();
        socket.QueueBinary(frame);
        await WaitUntilAsync(() => received.Count > 0);

        Assert.Single(received);
        // 逐字节相等：客户端只负责把整帧递出去，解码是接收侧接缝的事。
        Assert.Equal(frame, received[0]);
    }

    [Fact]
    public async Task BinaryFrame_DoesNotSwallowTheFollowingTextMessage()
    {
        var (socket, client) = NewPair(AudioCapable);
        await using var _ = client;

        var audioCount = 0;
        var mediaTitles = new List<string>();
        client.AudioFrameReceived += (_, _) => audioCount++;
        client.MediaReceived += (_, e) => mediaTitles.Add(e.Media.Title ?? "");

        client.Start();
        await socket.WaitForSendsAsync(2);

        // 收循环从「只收文本」改成「收全类型」后最容易坏的地方：
        // 处理完二进制若忘了 continue 而是落到文本分支，或反过来提前 return，
        // 后续的 JSON 事件就被吞掉——表现为「放着音乐时媒体信息不更新了」。
        socket.QueueBinary(AudioFrame());
        socket.QueueMediaUpdated(seq: 1, title: "After Binary");
        await WaitUntilAsync(() => audioCount > 0 && mediaTitles.Count > 0);

        Assert.Equal(1, audioCount);
        Assert.Equal(["After Binary"], mediaTitles);
    }

    [Fact]
    public async Task TextOnlyTraffic_NeverRaisesAudioFrameReceived()
    {
        var (socket, client) = NewPair(AudioCapable);
        await using var _ = client;

        var audioCount = 0;
        client.AudioFrameReceived += (_, _) => audioCount++;

        client.Start();
        await socket.WaitForSendsAsync(2);

        var mediaCount = 0;
        client.MediaReceived += (_, _) => mediaCount++;
        socket.QueueMediaUpdated(seq: 1, title: "Song");
        await WaitUntilAsync(() => mediaCount > 0);

        Assert.Equal(0, audioCount);
    }

    [Fact]
    public async Task BinaryDuringHandshake_IsSkippedAndHandshakeStillCompletes()
    {
        var socket = new ScriptedClientSocket();

        // 二进制夹在 hello 与 auth_ok 之间。握手路径仍用 ReceiveTextAsync，
        // 它会跳过二进制继续等文本——比断连宽容，且服务端在 auth_ok 前本就不该发音频。
        socket.QueueRaw(BuildHello(epoch: 1, AudioCapable));
        socket.QueueBinary(AudioFrame());
        socket.QueueMessage(MediaLinkProtocol.TypeAuthOk);
        socket.QueueMessage(MediaLinkProtocol.TypeSubscribeOk);

        await using var client = new MediaLinkClient(new MediaLinkClientOptions
        {
            Endpoint = new Uri("ws://127.0.0.1:1/v1/ws"),
            Token = "tok",
            InitialRetryDelay = TimeSpan.FromMilliseconds(50),
            SocketFactory = () => socket
        });

        var audioCount = 0;
        client.AudioFrameReceived += (_, _) => audioCount++;

        client.Start();
        await socket.WaitForSendsAsync(2);
        await WaitUntilAsync(() => client.IsConnected);

        Assert.True(client.IsConnected);
        Assert.Equal(0, audioCount);
    }

    // ---- 运行中切换订阅 ----

    [Fact]
    public async Task EnablingAudioWhileConnected_ResubscribesAndStartsPlayback()
    {
        var (socket, client) = NewPair(AudioCapable);
        await using var _ = client;

        client.Start();
        await socket.WaitForSendsAsync(2);

        await client.SetAudioSubscribedAsync(true);
        await socket.WaitForSendsAsync(2);

        // 全量三频道而非增量：subscribe 是整体替换，只发 audio 会把 media 与 lyrics 退订掉。
        var channels = SubscribedChannels(socket);
        Assert.Equal(
            [MediaLinkProtocol.ChannelMedia, MediaLinkProtocol.ChannelLyrics, MediaLinkProtocol.ChannelAudio],
            channels);
        Assert.True(Sent(socket, MediaLinkProtocol.TypeAudioPlayStart));
    }

    [Fact]
    public async Task DisablingAudioWhileConnected_ResubscribesWithoutAudioAndStops()
    {
        var (socket, client) = NewPair(AudioCapable);
        await using var _ = client;

        await client.SetAudioSubscribedAsync(true);
        client.Start();
        await socket.WaitForSendsAsync(3);

        await client.SetAudioSubscribedAsync(false);
        await socket.WaitForSendsAsync(2);

        Assert.Equal(
            [MediaLinkProtocol.ChannelMedia, MediaLinkProtocol.ChannelLyrics],
            SubscribedChannels(socket));
        Assert.True(Sent(socket, MediaLinkProtocol.TypeAudioPlayStop));
    }

    [Fact]
    public async Task SettingTheSameValueTwice_DoesNotResubscribe()
    {
        var (socket, client) = NewPair(AudioCapable);
        await using var _ = client;

        await client.SetAudioSubscribedAsync(true);
        client.Start();
        await socket.WaitForSendsAsync(3);
        var sentAfterFirst = socket.Sent.Count;

        await client.SetAudioSubscribedAsync(true);

        // 需求侧会在状态变化时无脑重算并调用，重复传相同值必须是 no-op——
        // 否则每次需求事件都会多一轮 subscribe 往返。
        Assert.Equal(sentAfterFirst, socket.Sent.Count);
    }

    [Fact]
    public async Task EnablingAudioWhileDisconnected_DoesNotThrowAndAppliesOnNextConnect()
    {
        var (socket, client) = NewPair(AudioCapable);
        await using var _ = client;

        // 组件可能在断线期间挂载。若此时丢掉意愿，得等用户手动重开组件才恢复。
        await client.SetAudioSubscribedAsync(true);

        client.Start();
        await socket.WaitForSendsAsync(3);

        Assert.Contains(MediaLinkProtocol.ChannelAudio, SubscribedChannels(socket));
    }

    [Fact]
    public async Task SubscribingFromTheConnectionStateCallback_ActuallySendsSubscribeWithAudio()
    {
        // 上游服务在连接态回调里重算订阅意愿，故「握手刚完成」这一刻必须已经能发出控制帧。
        // 这里在回调里就地阻塞调用，是为了把那个只有两条字段赋值宽的窗口变成确定性的：
        // 生产代码经 Task.Run 派发，同样的顺序错误在那里只是低概率竞态，测不稳。
        //
        // 顺序错了的表现是静默的：意愿旗标已置真，subscribe 却没发出去，
        // 于是上游起了采集而本会话没订到 audio 频道，广播跳过它；
        // 而相同值去重会让后续重算全部提前返回，只能等重连才恢复。
        var (socket, client) = NewPair(AudioCapable);
        await using var _ = client;

        var subscribeFailure = (Exception?)null;
        var switched = false;
        client.ConnectionStateChanged += (_, _) =>
        {
            if (!client.IsConnected || switched)
            {
                return;
            }

            switched = true;
            try
            {
                client.SetAudioSubscribedAsync(true).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                subscribeFailure = ex;
            }
        };

        client.Start();

        // auth、握手 subscribe、回调补发的 subscribe、audio.play_start。
        await socket.WaitForSendsAsync(4);

        Assert.Null(subscribeFailure);

        // 先钉「确实又发了一条 subscribe」再看内容：只断言含 audio 的话，
        // 一条都没补发时读到的是握手那条，判据就落到了别的帧上。
        var subscribes = socket.Sent
            .Where(s => s.Contains($"\"{MediaLinkProtocol.TypeSubscribe}\""))
            .ToArray();
        Assert.Equal(2, subscribes.Length);

        Assert.Contains(MediaLinkProtocol.ChannelAudio, SubscribedChannels(socket));
        Assert.True(Sent(socket, MediaLinkProtocol.TypeAudioPlayStart));
    }

    [Fact]
    public async Task AudioIntent_SurvivesReconnect()
    {
        var socket = new ScriptedClientSocket();
        socket.QueueRaw(BuildHello(epoch: 1, AudioCapable));
        socket.QueueMessage(MediaLinkProtocol.TypeAuthOk);
        socket.QueueMessage(MediaLinkProtocol.TypeSubscribeOk);
        socket.QueueClose();

        // 同一个 socket 实例会被重连复用，于是接着消费下半段脚本。
        socket.QueueRaw(BuildHello(epoch: 2, AudioCapable));
        socket.QueueMessage(MediaLinkProtocol.TypeAuthOk);
        socket.QueueMessage(MediaLinkProtocol.TypeSubscribeOk);

        await using var client = new MediaLinkClient(new MediaLinkClientOptions
        {
            Endpoint = new Uri("ws://127.0.0.1:1/v1/ws"),
            Token = "tok",
            InitialRetryDelay = TimeSpan.FromMilliseconds(20),
            MaxRetryDelay = TimeSpan.FromMilliseconds(50),
            SocketFactory = () => socket
        });

        await client.SetAudioSubscribedAsync(true);
        client.Start();

        // 两轮握手各 auth + subscribe + play_start = 6 条。
        await socket.WaitForSendsAsync(6);
        await WaitUntilAsync(() => socket.ConnectCount >= 2);

        // 重连后意愿仍在：否则断线一次就永久失去频谱，而用户什么都没做。
        Assert.True(socket.ConnectCount >= 2);
        Assert.Contains(MediaLinkProtocol.ChannelAudio, SubscribedChannels(socket));
        Assert.Equal(
            2,
            socket.Sent.Count(s => s.Contains($"\"{MediaLinkProtocol.TypeAudioPlayStart}\"")));
    }
}
