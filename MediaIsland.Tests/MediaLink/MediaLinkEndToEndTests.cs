using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Lyrics.Models;
using MediaIsland.Services.Media;
using MediaIsland.Services.MediaLink;
using MediaIsland.Services.MediaLink.Mapping;
using MediaIsland.Services.MediaLink.Protocol;
using MediaIsland.Models;
using Xunit;

namespace MediaIsland.Tests.MediaLink;

/// <summary>
/// 走真实回环 TCP 的端到端测试。回环端口与 accept 队列是进程级共享资源，
/// 且认证限速按 IP 记账（同一进程内所有连接都来自 127.0.0.1），
/// 故整类串行执行，避免测试间通过这些共享状态相互干扰。
/// </summary>
[Collection(nameof(MediaLinkEndToEndTests))]
[CollectionDefinition(nameof(MediaLinkEndToEndTests), DisableParallelization = true)]
public class MediaLinkEndToEndTests
{
    [Fact]
    public async Task FullFlow_ConnectAuthSubscribeReceiveUnsubscribe_StopReleasesPort()
    {
        // Arrange: let the OS assign a free port
        var port = 0;

        var media = new E2EFakeMediaService();
        var lyrics = new LyricsSearchService([], [], () => new LyricsSourceSettings());
        var store = new MediaLinkInjectionStore();
        var settings = new PluginSettings
        {
            MediaLinkMediaSourceMode = MediaLinkMediaSourceMode.PlatformOnly,
            MediaLinkPushUsesEffective = true
        };
        using var coordinator = new MediaSourceCoordinator(media, lyrics, store, () => settings);

        var hub = new MediaLinkSessionHub();
        using var publisher = new MediaLinkStatePublisher(coordinator, hub, timelineMinIntervalMs: () => 0);
        publisher.Start();
        var token = "test-token-123";
        var server = new MediaLinkServer(
            hub,
            session => publisher.PublishSnapshotAsync(session),
            () => token,
            coordinator: coordinator,
            logger: null);

        await server.StartAsync("127.0.0.1", port);
        Assert.True(server.IsRunning);
        // Extract actual port from endpoint
        var endpoint = server.Endpoint;
        var actualPort = int.Parse(endpoint!.Split(':')[2].Split('/')[0]);

        // Act: connect a real WebSocket client
        using var client = new ClientWebSocket();
        client.Options.KeepAliveInterval = TimeSpan.FromSeconds(5);
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{actualPort}/v1/ws"), CancellationToken.None);
        Assert.Equal(WebSocketState.Open, client.State);

        // Receive server.hello
        var hello = await ReceiveJsonAsync(client);
        Assert.Equal(MediaLinkProtocol.TypeEvent, hello.GetProperty("type").GetString());
        Assert.Equal(MediaLinkProtocol.EventServerHello, hello.GetProperty("name").GetString());

        // 能力声明必须真的出现在线上报文里：客户端据此决定是否订阅 audio，
        // 服务端漏填不会报错，只会让音频功能静默失效。
        var helloPayload = hello.GetProperty("payload");
        Assert.True(helloPayload.TryGetProperty("capabilities", out var capabilities), "server.hello 未声明 capabilities");
        Assert.Contains(
            MediaLinkProtocol.CapabilityAudio,
            capabilities.EnumerateArray().Select(item => item.GetString()));
        Assert.True(helloPayload.TryGetProperty("audio", out var audioFormat), "server.hello 未声明 audio 线格式");
        Assert.Equal(48000, audioFormat.GetProperty("sampleRate").GetInt32());
        Assert.Equal(2, audioFormat.GetProperty("channels").GetInt32());
        Assert.Equal("s16le", audioFormat.GetProperty("format").GetString());

        // Send auth
        await SendJsonAsync(client, new { type = "auth", id = "a1", v = 1, ts = NowMs(), payload = new { token } });
        var authOk = await ReceiveJsonAsync(client);
        Assert.Equal(MediaLinkProtocol.TypeAuthOk, authOk.GetProperty("type").GetString());

        // Subscribe
        await SendJsonAsync(client, new { type = "subscribe", id = "s1", v = 1, ts = NowMs(), payload = new { channels = new[] { "media", "lyrics" } } });
        var subOk = await ReceiveJsonAsync(client);
        Assert.Equal(MediaLinkProtocol.TypeSubscribeOk, subOk.GetProperty("type").GetString());

        // Receive snapshots (media.updated and/or lyrics.updated)
        var snapshotNames = new List<string>();
        for (var i = 0; i < 2; i++)
        {
            try
            {
                var snap = await ReceiveJsonAsync(client, timeoutMs: 1000);
                snapshotNames.Add(snap.GetProperty("name").GetString()!);
            }
            catch (OperationCanceledException) { break; }
        }
        Assert.Contains(MediaLinkProtocol.EventMediaUpdated, snapshotNames);

        // Trigger media change
        var sample = new MediaInfo("test-app", "TestSong", "TestArtist", "Album",
            TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(3),
            new MediaPlaybackInfo(MediaPlaybackState.Playing), null, null);
        media.Raise(sample, MediaInfoChangeKind.MediaProperties);

        // Receive incremental media.updated
        var mediaEvent = await ReceiveJsonAsync(client, timeoutMs: 5000);
        Assert.Equal(MediaLinkProtocol.EventMediaUpdated, mediaEvent.GetProperty("name").GetString());
        Assert.True(mediaEvent.GetProperty("seq").GetInt64() > 0);
        var payload = mediaEvent.GetProperty("payload");
        Assert.Equal("TestSong", payload.GetProperty("title").GetString());
        Assert.Equal("TestArtist", payload.GetProperty("artist").GetString());
        Assert.True(payload.TryGetProperty("trackToken", out _));
        Assert.True(payload.TryGetProperty("positionCapturedAtMs", out _));

        // Trigger another change, verify seq increases
        media.Raise(sample with { Position = TimeSpan.FromSeconds(60) }, MediaInfoChangeKind.Timeline);
        var mediaEvent2 = await ReceiveJsonAsync(client, timeoutMs: 5000);
        Assert.True(mediaEvent2.TryGetProperty("seq", out var seq2) && seq2.GetInt64() > mediaEvent.GetProperty("seq").GetInt64(),
            $"Expected seq in mediaEvent2, got: {mediaEvent2}");

        // Unsubscribe from lyrics
        await SendJsonAsync(client, new { type = "unsubscribe", id = "u1", v = 1, ts = NowMs(), payload = new { channels = new[] { "lyrics" } } });
        // Read messages until we find unsubscribe_ok (may receive trailing-edge events first)
        JsonElement unsubOk;
        while (true)
        {
            unsubOk = await ReceiveJsonAsync(client, timeoutMs: 5000);
            if (unsubOk.GetProperty("type").GetString() == MediaLinkProtocol.TypeUnsubscribeOk) break;
        }
        Assert.Equal(MediaLinkProtocol.TypeUnsubscribeOk, unsubOk.GetProperty("type").GetString());

        // Close client
        try { await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); } catch { }

        // Stop server
        await server.StopAsync();
        Assert.False(server.IsRunning);

        // Verify port is released
        try
        {
            var probe = new TcpListener(IPAddress.Loopback, port);
            probe.Start();
            probe.Stop();
        }
        catch
        {
            Assert.Fail("Port not released after server stop");
        }
    }

    [Fact]
    public async Task PingPong_WorksOverRealConnection()
    {
        var hub = new MediaLinkSessionHub();
        var server = new MediaLinkServer(hub, _ => Task.CompletedTask, () => "tok");
        await server.StartAsync("127.0.0.1", 0);
        var actualPort = int.Parse(server.Endpoint!.Split(':')[2].Split('/')[0]);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{actualPort}/v1/ws"), CancellationToken.None);

        // hello
        await ReceiveJsonAsync(client);

        // auth
        await SendJsonAsync(client, new { type = "auth", id = "a1", v = 1, ts = NowMs(), payload = new { token = "tok" } });
        await ReceiveJsonAsync(client);

        // ping
        await SendJsonAsync(client, new { type = "ping", id = "p1", v = 1, ts = NowMs() });
        var pong = await ReceiveJsonAsync(client);
        Assert.Equal(MediaLinkProtocol.TypePong, pong.GetProperty("type").GetString());
        Assert.Equal("p1", pong.GetProperty("id").GetString());

        try { await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); } catch { }
        await server.StopAsync();
    }

    [Fact]
    public async Task ServerStop_SendsGoingAwayToConnectedClient()
    {
        var hub = new MediaLinkSessionHub();
        var server = new MediaLinkServer(hub, _ => Task.CompletedTask, () => "tok");
        await server.StartAsync("127.0.0.1", 0);
        var actualPort = int.Parse(server.Endpoint!.Split(':')[2].Split('/')[0]);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{actualPort}/v1/ws"), CancellationToken.None);
        await ReceiveJsonAsync(client);
        await SendJsonAsync(client, new { type = "auth", id = "a1", v = 1, ts = NowMs(), payload = new { token = "tok" } });
        await ReceiveJsonAsync(client);

        await server.StopAsync();

        // 停服后客户端应收到关闭帧而非被硬断开：读到 Close 消息，且状态码为 1001。
        var buffer = new byte[4096];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (client.State == WebSocketState.Open)
        {
            var result = await client.ReceiveAsync(buffer, cts.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }
        }

        Assert.Equal((WebSocketCloseStatus)1001, client.CloseStatus);
    }

    [Fact]
    public async Task MediaLinkClient_AgainstRealServer_ReceivesMediaAndLyrics()
    {
        // 两端都是本仓库的实现：客户端的协议假设若与服务端不一致，这里会暴露。
        var media = new E2EFakeMediaService();
        var lyrics = new LyricsSearchService([], [], () => new LyricsSourceSettings());
        var settings = new PluginSettings
        {
            MediaLinkMediaSourceMode = MediaLinkMediaSourceMode.PlatformOnly,
            MediaLinkPushUsesEffective = true
        };
        using var coordinator = new MediaSourceCoordinator(media, lyrics, new MediaLinkInjectionStore(), () => settings);
        var hub = new MediaLinkSessionHub();
        using var publisher = new MediaLinkStatePublisher(coordinator, hub, timelineMinIntervalMs: () => 0);
        publisher.Start();

        var server = new MediaLinkServer(hub, s => publisher.PublishSnapshotAsync(s), () => "tok", coordinator: coordinator);
        await server.StartAsync("127.0.0.1", 0);
        var port = int.Parse(server.Endpoint!.Split(':')[2].Split('/')[0]);

        await using var client = new MediaLinkClient(new MediaLinkClientOptions
        {
            Endpoint = new Uri($"ws://127.0.0.1:{port}/v1/ws"),
            Token = "tok",
            InitialRetryDelay = TimeSpan.FromMilliseconds(100)
        });

        var received = new List<MediaLinkMediaDto>();
        var gate = new object();
        client.MediaReceived += (_, e) => { lock (gate) received.Add(e.Media); };

        client.Start();
        await WaitUntilAsync(() => client.IsConnected);

        media.Raise(
            new MediaInfo("app", "E2ESong", "E2EArtist", "Album",
                TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(3),
                new MediaPlaybackInfo(MediaPlaybackState.Playing), null, null),
            MediaInfoChangeKind.MediaProperties);

        await WaitUntilAsync(() =>
        {
            lock (gate) return received.Any(m => m.Title == "E2ESong");
        });

        MediaLinkMediaDto match;
        lock (gate) match = received.First(m => m.Title == "E2ESong");

        Assert.Equal("E2EArtist", match.Artist);
        Assert.False(string.IsNullOrEmpty(match.TrackToken));
        // 时间基准必须完整传到客户端，否则转发时算不出 positionAgeMs。
        Assert.True(match.PositionCapturedAtMs > 0);
        Assert.True(match.ServerTimeMs > 0);

        await client.StopAsync();
        await server.StopAsync();
    }

    [Fact]
    public async Task TwoInstanceChain_ForwardsMediaWithPreservedTimeBase()
    {
        // 上游实例 → 客户端 → 下游实例的注入存储。这是"另一台 ClassIsland
        // 消费本实例"的完整链路，也是 positionAgeMs 唯一真正被用到的地方。
        var upstreamMedia = new E2EFakeMediaService();
        var lyrics = new LyricsSearchService([], [], () => new LyricsSourceSettings());
        var settings = new PluginSettings
        {
            MediaLinkMediaSourceMode = MediaLinkMediaSourceMode.PlatformOnly,
            MediaLinkPushUsesEffective = true
        };
        using var upstreamCoordinator = new MediaSourceCoordinator(
            upstreamMedia, lyrics, new MediaLinkInjectionStore(), () => settings);
        var hub = new MediaLinkSessionHub();
        using var publisher = new MediaLinkStatePublisher(upstreamCoordinator, hub, timelineMinIntervalMs: () => 0);
        publisher.Start();

        var server = new MediaLinkServer(hub, s => publisher.PublishSnapshotAsync(s), () => "tok", coordinator: upstreamCoordinator);
        await server.StartAsync("127.0.0.1", 0);
        var port = int.Parse(server.Endpoint!.Split(':')[2].Split('/')[0]);

        // 下游实例的注入存储，用可控时钟以便断言位置
        var downstreamTick = 100_000L;
        var downstreamStore = new MediaLinkInjectionStore(tickProvider: () => downstreamTick);

        await using var client = new MediaLinkClient(new MediaLinkClientOptions
        {
            Endpoint = new Uri($"ws://127.0.0.1:{port}/v1/ws"),
            Token = "tok",
            InitialRetryDelay = TimeSpan.FromMilliseconds(100)
        }, tickProvider: () => downstreamTick);

        client.MediaReceived += (_, e) =>
        {
            // 模拟收帧后经过 200ms 才完成转发
            var elapsed = (downstreamTick + 200) - e.ReceivedAtTick;
            var payload = MediaLinkDtoMapper.ToInjectPayload(e.Media, elapsed);
            downstreamStore.TrySetMedia(payload, out string? _);
        };

        client.Start();
        await WaitUntilAsync(() => client.IsConnected);

        upstreamMedia.Raise(
            new MediaInfo("upstream-app", "ChainSong", "Artist", null,
                TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(3),
                new MediaPlaybackInfo(MediaPlaybackState.Playing), null, null),
            MediaInfoChangeKind.MediaProperties);

        await WaitUntilAsync(() => downstreamStore.GetMediaSnapshot()?.Title == "ChainSong");

        var snapshot = downstreamStore.GetMediaSnapshot()!;
        Assert.Equal("ChainSong", snapshot.Title);
        Assert.Equal("upstream-app", snapshot.SourceApp);
        // 位置至少补回了那 200ms 的转发耗时，而不是停在上游采样的 30s。
        Assert.True(
            snapshot.Position >= TimeSpan.FromMilliseconds(30_200),
            $"位置未回补转发耗时：{snapshot.Position}");

        await client.StopAsync();
        await server.StopAsync();
    }

    [Fact]
    public async Task UpstreamHostedService_AgainstRealServer_WritesIntoInjectionStore()
    {
        // 接线的实际验收点：启用上游后，对方的媒体应当成为本机的有效媒体。
        // 前面的测试只验到"客户端收到了"，没验到"配置驱动的服务把它用起来了"。
        var upstreamMedia = new E2EFakeMediaService();
        var lyrics = new LyricsSearchService([], [], () => new LyricsSourceSettings());
        var upstreamSettings = new PluginSettings
        {
            MediaLinkMediaSourceMode = MediaLinkMediaSourceMode.PlatformOnly,
            MediaLinkPushUsesEffective = true
        };
        using var upstreamCoordinator = new MediaSourceCoordinator(
            upstreamMedia, lyrics, new MediaLinkInjectionStore(), () => upstreamSettings);
        var hub = new MediaLinkSessionHub();
        using var publisher = new MediaLinkStatePublisher(upstreamCoordinator, hub, timelineMinIntervalMs: () => 0);
        publisher.Start();

        var server = new MediaLinkServer(hub, s => publisher.PublishSnapshotAsync(s), () => "up-tok", coordinator: upstreamCoordinator);
        await server.StartAsync("127.0.0.1", 0);
        var port = int.Parse(server.Endpoint!.Split(':')[2].Split('/')[0]);

        // 下游：用简写地址，顺便验证补全逻辑在真实链路上成立
        var downstreamSettings = new PluginSettings
        {
            MediaLinkMediaSourceMode = MediaLinkMediaSourceMode.ExternalPreferred,
            MediaLinkUpstreamIsEnabled = true,
            MediaLinkUpstreamEndpoint = $"127.0.0.1:{port}",
            MediaLinkUpstreamToken = "up-tok"
        };
        var downstreamStore = new MediaLinkInjectionStore();
        var downstreamMedia = new E2EFakeMediaService();
        using var downstreamCoordinator = new MediaSourceCoordinator(
            downstreamMedia, lyrics, downstreamStore, () => downstreamSettings);

        using var upstreamService = new MediaLinkUpstreamHostedService(
            downstreamStore, downstreamCoordinator, () => downstreamSettings);

        await upstreamService.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => upstreamService.IsConnected);

        upstreamMedia.Raise(
            new MediaInfo("upstream-app", "WiredSong", "WiredArtist", null,
                TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(4),
                new MediaPlaybackInfo(MediaPlaybackState.Playing), null, null),
            MediaInfoChangeKind.MediaProperties);

        await WaitUntilAsync(() => downstreamStore.GetMediaSnapshot()?.Title == "WiredSong");

        Assert.Equal("WiredSong", downstreamStore.GetMediaSnapshot()!.Title);
        // 外部优先模式下，合成结果也应当采用上游数据
        Assert.Equal("WiredSong", downstreamCoordinator.ComposeMedia()?.Title);

        await upstreamService.StopAsync(CancellationToken.None);
        await server.StopAsync();
    }

    [Fact]
    public async Task UpstreamHostedService_AfterHandshake_UpstreamSeesAudioSubscription()
    {
        // 补订阅只能由握手完成后的连接态回调来做：MediaLinkClient.Start() 是 fire-and-forget，
        // 返回时握手尚未开始，仲裁里「连上了」与「对端支持 audio」两项必为假，
        // 配置生效那一刻补不到。故这条接线断了不会有任何报错，只会静默收不到音频。
        //
        // 验收落在上游会话侧而非客户端旗标：旗标为真却没发出 subscribe 也是一种失败模式，
        // 只有上游真的把 audio 记进这个会话的频道集，广播才会带上它。
        var upstreamMedia = new E2EFakeMediaService();
        var lyrics = new LyricsSearchService([], [], () => new LyricsSourceSettings());
        var upstreamSettings = new PluginSettings
        {
            MediaLinkMediaSourceMode = MediaLinkMediaSourceMode.PlatformOnly,
            MediaLinkPushUsesEffective = true
        };
        using var upstreamCoordinator = new MediaSourceCoordinator(
            upstreamMedia, lyrics, new MediaLinkInjectionStore(), () => upstreamSettings);
        var hub = new MediaLinkSessionHub();
        using var publisher = new MediaLinkStatePublisher(upstreamCoordinator, hub, timelineMinIntervalMs: () => 0);
        publisher.Start();

        var server = new MediaLinkServer(
            hub, s => publisher.PublishSnapshotAsync(s), () => "up-tok", coordinator: upstreamCoordinator);
        await server.StartAsync("127.0.0.1", 0);
        var port = int.Parse(server.Endpoint!.Split(':')[2].Split('/')[0]);

        var downstreamSettings = new PluginSettings
        {
            MediaLinkMediaSourceMode = MediaLinkMediaSourceMode.ExternalPreferred,
            MediaLinkUpstreamIsEnabled = true,
            MediaLinkUpstreamEndpoint = $"127.0.0.1:{port}",
            MediaLinkUpstreamToken = "up-tok"
        };
        var downstreamStore = new MediaLinkInjectionStore();
        using var downstreamCoordinator = new MediaSourceCoordinator(
            new E2EFakeMediaService(), lyrics, downstreamStore, () => downstreamSettings);

        // 仲裁还要求「当前生效的媒体确实来自上游」，而这一项在连接态回调触发的当下求值——
        // 那一刻上游快照还没抵达。故先注入一条让判据在握手完成前就成立；
        // 上游放同一首在播，快照抵达覆盖注入后判据也不会翻回假。
        upstreamMedia.Raise(
            new MediaInfo("upstream-app", "AudioWireSong", "AudioWireArtist", null,
                TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(4),
                new MediaPlaybackInfo(MediaPlaybackState.Playing), null, null),
            MediaInfoChangeKind.MediaProperties);
        Assert.True(downstreamStore.TrySetMedia(new MediaLinkMediaInjectPayload
        {
            SourceApp = "upstream-app",
            Title = "AudioWireSong",
            Artist = "AudioWireArtist",
            PlaybackState = "Playing"
        }, out _));
        Assert.True(downstreamCoordinator.IsExternalMediaEffective, "前置不成立：生效媒体应判为来自上游");

        // 「要不要」的三个来源里取转发这一路：另两路要真频谱组件或真播放设备。
        using var upstreamService = new MediaLinkUpstreamHostedService(
            downstreamStore, downstreamCoordinator, () => downstreamSettings,
            downstreamAudioDemandAccessor: () => true);

        await upstreamService.StartAsync(CancellationToken.None);

        // 先钉地基再验目标：一条都没连上、或连上了却没走到 subscribe，
        // 在「频道集里没有 audio」这一个断言下与「订阅漏了 audio」长得一模一样。
        await WaitUntilAsync(() => hub.Sessions.Count == 1);
        var session = Assert.Single(hub.Sessions);
        await WaitUntilAsync(() => session.IsSubscribedTo(MediaLinkProtocol.ChannelMedia));
        Assert.True(session.IsSubscribedTo(MediaLinkProtocol.ChannelMedia), "握手没走到 subscribe");

        // 验收点本身。
        await WaitUntilAsync(() => session.IsSubscribedToAudio);
        Assert.True(
            session.IsSubscribedToAudio,
            $"握手完成后上游未收到 audio 订阅，会话当前频道：[{string.Join(", ", session.Channels)}]");

        await upstreamService.StopAsync(CancellationToken.None);
        await server.StopAsync();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task AudioFrame_OverRealWebSocket_ArrivesByteIdentical()
    {
        // 用真实回环连接而非 fake：这条路径上的分片组装、ToArray() 副本语义与编解码
        // 往返一致性都只有真 socket 才验得到。fake 直接传引用，会掩盖共享缓冲被覆写的问题。
        var hub = new MediaLinkSessionHub();
        var server = new MediaLinkServer(hub, _ => Task.CompletedTask, () => "tok");
        await server.StartAsync("127.0.0.1", 0);
        var port = int.Parse(server.Endpoint!.Split(':')[2].Split('/')[0]);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/v1/ws"), CancellationToken.None);
        await ReceiveJsonAsync(client); // hello
        await SendJsonAsync(client, new { type = "auth", id = "a1", v = 1, ts = NowMs(), payload = new { token = "tok" } });
        await ReceiveJsonAsync(client); // auth_ok
        await SendJsonAsync(client, new { type = "subscribe", id = "s1", v = 1, ts = NowMs(), payload = new { channels = new[] { "audio" } } });
        await ReceiveJsonAsync(client); // subscribe_ok

        // 帧大小刻意远低于 MaxMessageBytes（2 MiB），同时足以跨越 64 KiB 接收缓冲触发分片组装。
        var pcm = new byte[96 * 1024];
        Random.Shared.NextBytes(pcm);
        var original = MediaLinkAudioFrame.Encode(
            new MediaLinkAudioFrameHeader(1234, 5678, 9012, 99, "e2e-track", MediaLinkAudioFrameFlags.TrackStart),
            pcm);

        MediaLinkSession session = null!;
        await WaitUntilAsync(() => hub.Sessions.Count == 1);
        session = hub.Sessions.First();
        await WaitUntilAsync(() => session.IsSubscribedToAudio);
        await session.EnqueueAudioAsync(original, CancellationToken.None);

        var arrived = await ReceiveBinaryAsync(client);
        Assert.Equal(original, arrived);

        // 解码在同步局部函数内完成：ReadOnlySpan 是 ref struct，C# 12 不允许它出现在 async 方法体中。
        static (MediaLinkAudioFrameHeader Header, byte[] Pcm) Decode(byte[] frame)
        {
            Assert.True(MediaLinkAudioFrame.TryDecode(frame, out var header, out var pcm, out _));
            return (header, pcm.ToArray());
        }

        var (decodedHeader, decodedPcm) = Decode(arrived);
        Assert.Equal("e2e-track", decodedHeader.TrackToken);
        Assert.Equal(99u, decodedHeader.Seq);
        Assert.Equal(1234, decodedHeader.StartPositionMs);
        Assert.Equal(MediaLinkAudioFrameFlags.TrackStart, decodedHeader.Flags);
        Assert.Equal(pcm, decodedPcm);

        try { await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); } catch { }
        await server.StopAsync();
    }

    [Fact]
    public async Task InboundAudioFrame_OverRealWebSocket_ReachesCallbackByteIdentical()
    {
        // 反向：客户端注入音频帧。服务端接收循环把 message.Binary 交给解码器，
        // 而解码出的 PCM 是入参的零拷贝切片——若拷出时机不对，这里会读到被覆写的数据。
        var hub = new MediaLinkSessionHub();
        byte[]? receivedPcm = null;
        MediaLinkAudioFrameHeader? receivedHeader = null;
        var server = new MediaLinkServer(
            hub,
            _ => Task.CompletedTask,
            () => "tok",
            onAudioFrameAsync: (header, pcm) =>
            {
                receivedHeader = header;
                receivedPcm = pcm;
                return Task.CompletedTask;
            });
        await server.StartAsync("127.0.0.1", 0);
        var port = int.Parse(server.Endpoint!.Split(':')[2].Split('/')[0]);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/v1/ws"), CancellationToken.None);
        await ReceiveJsonAsync(client); // hello
        await SendJsonAsync(client, new { type = "auth", id = "a1", v = 1, ts = NowMs(), payload = new { token = "tok" } });
        await ReceiveJsonAsync(client); // auth_ok

        var pcm = new byte[96 * 1024];
        Random.Shared.NextBytes(pcm);
        var frame = MediaLinkAudioFrame.Encode(
            new MediaLinkAudioFrameHeader(11, 22, 33, 44, "inbound-track", MediaLinkAudioFrameFlags.Silent),
            pcm);

        await client.SendAsync(frame, WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);

        await WaitUntilAsync(() => receivedPcm is not null);
        Assert.NotNull(receivedPcm);
        Assert.Equal("inbound-track", receivedHeader!.Value.TrackToken);
        Assert.Equal(44u, receivedHeader.Value.Seq);
        Assert.Equal(pcm, receivedPcm);

        try { await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); } catch { }
        await server.StopAsync();
    }

    [Fact]
    public async Task MediaLinkClientSocket_ReceivesBinaryFrame_NotSilentlyDropped()
    {
        // 客户端侧此前遇非 Text 就 continue，服务端发出的音频帧到了客户端会被直接丢掉。
        // 音频发出去等于没发——故接收链路必须一并验通。
        var hub = new MediaLinkSessionHub();
        var server = new MediaLinkServer(hub, _ => Task.CompletedTask, () => "tok");
        await server.StartAsync("127.0.0.1", 0);
        var port = int.Parse(server.Endpoint!.Split(':')[2].Split('/')[0]);

        await using var socket = new ClientWebSocketAdapter();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/v1/ws"), CancellationToken.None);
        await socket.ReceiveAsync(CancellationToken.None); // hello
        await socket.SendTextAsync(
            MediaLinkMessageSerializer.Serialize(MediaLinkMessageSerializer.Create(
                MediaLinkProtocol.TypeAuth, new MediaLinkAuthPayload { Token = "tok" }, id: "a1")),
            CancellationToken.None);
        await socket.ReceiveAsync(CancellationToken.None); // auth_ok
        await socket.SendTextAsync(
            MediaLinkMessageSerializer.Serialize(MediaLinkMessageSerializer.Create(
                MediaLinkProtocol.TypeSubscribe,
                new MediaLinkSubscribePayload { Channels = [MediaLinkProtocol.ChannelAudio] },
                id: "s1")),
            CancellationToken.None);
        await socket.ReceiveAsync(CancellationToken.None); // subscribe_ok

        await WaitUntilAsync(() => hub.Sessions.Count == 1);
        var session = hub.Sessions.First();
        await WaitUntilAsync(() => session.IsSubscribedToAudio);

        var frame = MediaLinkAudioFrame.Encode(
            new MediaLinkAudioFrameHeader(0, 0, 0, 5, "client-track", MediaLinkAudioFrameFlags.None),
            [1, 2, 3, 4]);
        await session.EnqueueAudioAsync(frame, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var message = await socket.ReceiveAsync(cts.Token);

        Assert.NotNull(message.Binary);
        Assert.Equal(frame, message.Binary);

        await server.StopAsync();
    }

    private static async Task<byte[]> ReceiveBinaryAsync(WebSocket ws, int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer, cts.Token);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("WebSocket closed unexpectedly");
            if (result.MessageType != WebSocketMessageType.Binary)
                throw new InvalidOperationException($"期望二进制帧，实际为 {result.MessageType}");
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        return ms.ToArray();
    }

    [Fact]
    public async Task RapidTrackChanges_TrackTokenMatchesSourceApp()
    {
        var hub = new MediaLinkSessionHub();
        var media = new E2EFakeMediaService();
        var lyrics = new LyricsSearchService([], [], () => new LyricsSourceSettings());
        var store = new MediaLinkInjectionStore();
        var settings = new PluginSettings
        {
            MediaLinkMediaSourceMode = MediaLinkMediaSourceMode.PlatformOnly,
            MediaLinkPushUsesEffective = true
        };
        using var coordinator = new MediaSourceCoordinator(media, lyrics, store, () => settings);
        using var publisher = new MediaLinkStatePublisher(coordinator, hub, timelineMinIntervalMs: () => 0);
        publisher.Start();

        var server = new MediaLinkServer(hub, session => publisher.PublishSnapshotAsync(session), () => "tok", coordinator: coordinator);
        await server.StartAsync("127.0.0.1", 0);
        var port = int.Parse(server.Endpoint!.Split(':')[2].Split('/')[0]);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/v1/ws"), CancellationToken.None);
        await ReceiveJsonAsync(client); // hello
        await SendJsonAsync(client, new { type = "auth", id = "a1", v = 1, ts = NowMs(), payload = new { token = "tok" } });
        await ReceiveJsonAsync(client); // auth_ok
        await SendJsonAsync(client, new { type = "subscribe", id = "s1", v = 1, ts = NowMs(), payload = new { channels = new[] { "media" } } });
        await ReceiveJsonAsync(client); // subscribe_ok

        // Rapid track changes: 10 songs in succession
        for (var i = 0; i < 10; i++)
        {
            var song = new MediaInfo("player", $"Song{i}", $"Artist{i}", null,
                TimeSpan.Zero, TimeSpan.FromMinutes(3),
                new MediaPlaybackInfo(MediaPlaybackState.Playing), null, null);
            media.Raise(song, MediaInfoChangeKind.MediaProperties);
            await Task.Delay(30);
        }

        // Drain all received events
        var received = new List<JsonElement>();
        try
        {
            while (true)
            {
                try { received.Add(await ReceiveJsonAsync(client, timeoutMs: 500)); }
                catch (OperationCanceledException) { break; }
            }
        }
        catch (WebSocketException) { } // connection may be aborted by server

        var mediaEvents = received.Where(e => e.TryGetProperty("name", out var n) && n.GetString() == MediaLinkProtocol.EventMediaUpdated).ToList();

        // 订阅时推送的快照若赶在首次切歌之前取样，此刻还没有任何媒体，会发出一条没有 payload
        // 的 media.updated，语义是「当前无播放」。它本就不该带 trackToken，必须先滤掉：
        // 否则断言结果取决于快照取样与首次切歌谁先跑，同一份代码会随机红绿。
        var trackEvents = mediaEvents
            .Where(e => e.TryGetProperty("payload", out var p)
                        && p.ValueKind == JsonValueKind.Object
                        && p.TryGetProperty("title", out _))
            .ToList();

        // 门槛必须落在过滤后的集合上。若仍只要求过滤前的总数，滤完一条不剩时测试照样绿，
        // 那是「被测代码什么都不做也能通过」的假绿，比随机红更难发现。
        Assert.True(trackEvents.Count >= 2,
            $"Expected at least 2 media events carrying a track from 10 track changes, got {trackEvents.Count}. media.updated: {mediaEvents.Count}, total received: {received.Count}");
        foreach (var evt in trackEvents)
        {
            // 过滤已保证 payload 存在，这里取用不会抛 KeyNotFoundException。
            Assert.True(evt.GetProperty("payload").TryGetProperty("trackToken", out _),
                $"media.updated described a track but carried no trackToken: {evt}");
        }

        try { await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); } catch { }
        await server.StopAsync();
    }

    [Fact]
    public async Task ServerHello_CarriesSessionEpoch_IncreasingAcrossRebuilds()
    {
        var hub = new MediaLinkSessionHub();
        var server = new MediaLinkServer(hub, _ => Task.CompletedTask, () => "tok");

        await server.StartAsync("127.0.0.1", 0);
        var firstEpoch = await ReadHelloEpochAsync(server);
        await server.StopAsync();

        // 重建 listener：seq 归零，客户端必须能靠 epoch 变化识别出这不是乱序
        await server.StartAsync("127.0.0.1", 0);
        var secondEpoch = await ReadHelloEpochAsync(server);
        await server.StopAsync();

        Assert.True(firstEpoch > 0, $"epoch 应为正数，实际 {firstEpoch}");
        Assert.True(secondEpoch > firstEpoch, $"重建后 epoch 应递增：{firstEpoch} -> {secondEpoch}");

        // HostedService 每次重载都新建 MediaLinkServer，故 epoch 必须跨实例单调，
        // 否则新实例从 1 重新开始，客户端无法识别重启。
        var replacement = new MediaLinkServer(hub, _ => Task.CompletedTask, () => "tok");
        await replacement.StartAsync("127.0.0.1", 0);
        var thirdEpoch = await ReadHelloEpochAsync(replacement);
        await replacement.StopAsync();

        Assert.True(thirdEpoch > secondEpoch, $"新 server 实例的 epoch 应继续递增：{secondEpoch} -> {thirdEpoch}");
    }

    private static async Task<long> ReadHelloEpochAsync(MediaLinkServer server)
    {
        var port = int.Parse(server.Endpoint!.Split(':')[2].Split('/')[0]);
        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/v1/ws"), CancellationToken.None);
        var hello = await ReceiveJsonAsync(client);
        Assert.Equal(MediaLinkProtocol.EventServerHello, hello.GetProperty("name").GetString());
        var epoch = hello.GetProperty("payload").GetProperty("sessionEpoch").GetInt64();
        try { await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); } catch { }
        return epoch;
    }

    [Fact]
    public async Task ThumbnailGet_OverWebSocket_ReturnsPayloadWithTrackToken()
    {
        var media = new E2EFakeMediaService();
        var lyrics = new LyricsSearchService([], [], () => new LyricsSourceSettings());
        var store = new MediaLinkInjectionStore();
        var settings = new PluginSettings { MediaLinkPushUsesEffective = true };
        using var coordinator = new MediaSourceCoordinator(media, lyrics, store, () => settings);
        media.Raise(
            new MediaInfo("player", "Song", "Artist", null, TimeSpan.Zero, TimeSpan.FromMinutes(3),
                new MediaPlaybackInfo(MediaPlaybackState.Playing), null, null),
            MediaInfoChangeKind.MediaProperties);

        var hub = new MediaLinkSessionHub();
        var server = new MediaLinkServer(hub, _ => Task.CompletedTask, () => "tok", coordinator: coordinator);
        await server.StartAsync("127.0.0.1", 0);
        var port = int.Parse(server.Endpoint!.Split(':')[2].Split('/')[0]);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/v1/ws"), CancellationToken.None);
        await ReceiveJsonAsync(client); // hello
        await SendJsonAsync(client, new { type = "auth", id = "a1", v = 1, ts = NowMs(), payload = new { token = "tok" } });
        await ReceiveJsonAsync(client); // auth_ok

        // 无需订阅任何频道，也无需把 Token 放进 URL
        await SendJsonAsync(client, new { type = "thumbnail.get", id = "th1", v = 1, ts = NowMs() });
        var response = await ReceiveJsonAsync(client);

        Assert.Equal(MediaLinkProtocol.TypeThumbnail, response.GetProperty("type").GetString());
        Assert.Equal("th1", response.GetProperty("id").GetString());
        var payload = response.GetProperty("payload");
        Assert.False(string.IsNullOrEmpty(payload.GetProperty("trackToken").GetString()));

        try { await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); } catch { }
        await server.StopAsync();
    }

    [Fact]
    public async Task Thumbnail_WrongToken_Returns401()
    {
        var hub = new MediaLinkSessionHub();
        var server = new MediaLinkServer(hub, _ => Task.CompletedTask, () => "right");
        await server.StartAsync("127.0.0.1", 0);
        var port = int.Parse(server.Endpoint!.Split(':')[2].Split('/')[0]);

        var response = await HttpGetAsync(port, "/v1/thumbnail?token=wrong");
        Assert.Contains("401", response);

        await server.StopAsync();
    }

    [Fact]
    public async Task Thumbnail_NoMedia_Returns404()
    {
        var media = new E2EFakeMediaService();
        var lyrics = new LyricsSearchService([], [], () => new LyricsSourceSettings());
        var store = new MediaLinkInjectionStore();
        var settings = new PluginSettings { MediaLinkPushUsesEffective = true };
        using var coordinator = new MediaSourceCoordinator(media, lyrics, store, () => settings);

        var hub = new MediaLinkSessionHub();
        var server = new MediaLinkServer(hub, _ => Task.CompletedTask, () => "tok", coordinator: coordinator);
        await server.StartAsync("127.0.0.1", 0);
        var port = int.Parse(server.Endpoint!.Split(':')[2].Split('/')[0]);

        var response = await HttpGetAsync(port, "/v1/thumbnail?token=tok");
        Assert.Contains("404", response);

        await server.StopAsync();
    }

    [Fact]
    public async Task Thumbnail_TrackTokenMismatch_Returns404()
    {
        var media = new E2EFakeMediaService();
        var lyrics = new LyricsSearchService([], [], () => new LyricsSourceSettings());
        var store = new MediaLinkInjectionStore();
        var settings = new PluginSettings { MediaLinkPushUsesEffective = true };
        using var coordinator = new MediaSourceCoordinator(media, lyrics, store, () => settings);
        media.Raise(
            new MediaInfo("app", "Song", "Artist", null, TimeSpan.Zero, TimeSpan.FromMinutes(3),
                new MediaPlaybackInfo(MediaPlaybackState.Playing), null, null),
            MediaInfoChangeKind.MediaProperties);

        var hub = new MediaLinkSessionHub();
        var server = new MediaLinkServer(hub, _ => Task.CompletedTask, () => "tok", coordinator: coordinator);
        await server.StartAsync("127.0.0.1", 0);
        var port = int.Parse(server.Endpoint!.Split(':')[2].Split('/')[0]);

        var response = await HttpGetAsync(port, "/v1/thumbnail?token=tok&t=stale-track-token");
        Assert.Contains("404", response);

        await server.StopAsync();
    }

    [Fact]
    public async Task Thumbnail_RoutedIndependentlyOfWebSocketUpgrade()
    {
        // 缩略图端点必须与 /v1/ws 分流：不带 WS 升级头也应得到 HTTP 响应而非 400。
        var hub = new MediaLinkSessionHub();
        var server = new MediaLinkServer(hub, _ => Task.CompletedTask, () => "tok");
        await server.StartAsync("127.0.0.1", 0);
        var port = int.Parse(server.Endpoint!.Split(':')[2].Split('/')[0]);

        var response = await HttpGetAsync(port, "/v1/thumbnail?token=tok");
        Assert.StartsWith("HTTP/1.1", response);
        Assert.DoesNotContain("501", response);
        Assert.Contains("no-store", response);

        await server.StopAsync();
    }

    private static async Task<string> HttpGetAsync(int port, string pathAndQuery)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await using var stream = client.GetStream();
        var request = Encoding.ASCII.GetBytes(
            $"GET {pathAndQuery} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(request);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cts.Token);
            if (read == 0) break;
            buffer.Write(chunk, 0, read);
        }

        return Encoding.Latin1.GetString(buffer.ToArray());
    }

    [Fact]
    public async Task AuthFailuresOverLimit_RejectNewConnectionsAtAccept()
    {
        var hub = new MediaLinkSessionHub();
        var server = new MediaLinkServer(hub, _ => Task.CompletedTask, () => "right-token");
        await server.StartAsync("127.0.0.1", 0);
        var port = int.Parse(server.Endpoint!.Split(':')[2].Split('/')[0]);

        // 连续用错误 Token 认证失败，直到达到限速阈值
        for (var i = 0; i < MediaLinkServer.AuthFailureLimit; i++)
        {
            using var bad = new ClientWebSocket();
            await bad.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/v1/ws"), CancellationToken.None);
            await ReceiveJsonAsync(bad); // hello
            await SendJsonAsync(bad, new { type = "auth", id = "a", v = 1, ts = NowMs(), payload = new { token = "wrong" } });
            try { await ReceiveJsonAsync(bad); } catch { /* auth_fail 后立即关闭 */ }
        }

        // 被限速的连接应在 accept 阶段直接关闭：读到 EOF，且没有任何 HTTP 响应字节
        using var blocked = new TcpClient();
        await blocked.ConnectAsync(IPAddress.Loopback, port);
        await using var stream = blocked.GetStream();
        var request = Encoding.ASCII.GetBytes(
            "GET /v1/ws HTTP/1.1\r\n" +
            $"Host: 127.0.0.1:{port}\r\n" +
            "Connection: Upgrade\r\nUpgrade: websocket\r\n" +
            "Sec-WebSocket-Version: 13\r\n" +
            "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n\r\n");
        try { await stream.WriteAsync(request); } catch (IOException) { /* 已被 RST */ }

        using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var buffer = new byte[64];
        int read;
        try
        {
            read = await stream.ReadAsync(buffer, readCts.Token);
        }
        catch (IOException)
        {
            read = 0; // RST 等同于拒绝
        }

        Assert.Equal(0, read);

        // 限速按 IP 记账，同进程内所有连接都来自 127.0.0.1：
        // 不清理会让后续测试的连接被误拒。
        server.ClearAuthFailures("127.0.0.1");
        await server.StopAsync();
    }

    private static async Task<JsonElement> ReceiveJsonAsync(WebSocket ws, int timeoutMs = 3000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer, cts.Token);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("WebSocket closed unexpectedly");
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        return JsonDocument.Parse(Encoding.UTF8.GetString(ms.ToArray())).RootElement;
    }

    private static async Task SendJsonAsync(WebSocket ws, object payload)
    {
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private sealed class E2EFakeMediaService : IMediaService
    {
        public event EventHandler<MediaInfoChangedEventArgs>? MediaInfoChanged;
        public MediaInfo? CurrentMediaInfo { get; set; }
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
        public Task EnsureStartedAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Raise(MediaInfo? info, MediaInfoChangeKind kind)
        {
            CurrentMediaInfo = info;
            MediaInfoChanged?.Invoke(this, new MediaInfoChangedEventArgs(info, kind));
        }
    }
}