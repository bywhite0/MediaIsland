using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using MediaIsland.Services.Audio;
using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Lyrics.Models;
using MediaIsland.Services.Media;
using MediaIsland.Services.Media.Platform;
using MediaIsland.Services.MediaLink;
using MediaIsland.Services.MediaLink.Mapping;
using MediaIsland.Services.MediaLink.Protocol;
using MediaIsland.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
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
        await ConnectBoundedAsync(client, new Uri($"ws://127.0.0.1:{actualPort}/v1/ws"));
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
        await CloseBoundedAsync(client);

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
        await ConnectBoundedAsync(client, new Uri($"ws://127.0.0.1:{actualPort}/v1/ws"));

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

        await CloseBoundedAsync(client);
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
        await ConnectBoundedAsync(client, new Uri($"ws://127.0.0.1:{actualPort}/v1/ws"));
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

        await StartBoundedAsync(upstreamService);
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

        await StopBoundedAsync(upstreamService);
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

        await StartBoundedAsync(upstreamService);

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

        await StopBoundedAsync(upstreamService);
        await server.StopAsync();
    }

    [Fact]
    public async Task DownstreamAudioDemand_AloneIsEnough_ToPullUpstreamSubscription()
    {
        // 转发这一路的需求只有一个来源：下游会话订阅 audio 并 play_start。
        // 它变化时若不通知上游服务重算，向上游的订阅就永远起不来，转发根本不会开始。
        //
        // 这个缺陷只在「静止」时可见：播放中上游每秒推一次时间轴，生效媒体随之变化，
        // 那条边会把订阅顺带救回来。故本用例刻意让上游放一首暂停的曲目——
        // 媒体快照此后恒等，协调器不再发生效媒体变化事件，中继节点也没有频谱组件，
        // 于是「下游要音频」是全过程唯一的状态变化，没有任何东西能替它触发重算。
        var lyrics = new LyricsSearchService([], [], () => new LyricsSourceSettings());

        // 上游节点
        var upstreamMedia = new E2EFakeMediaService();
        var upstreamSettings = new PluginSettings
        {
            MediaLinkMediaSourceMode = MediaLinkMediaSourceMode.PlatformOnly,
            MediaLinkPushUsesEffective = true
        };
        using var upstreamCoordinator = new MediaSourceCoordinator(
            upstreamMedia, lyrics, new MediaLinkInjectionStore(), () => upstreamSettings);
        var upstreamHub = new MediaLinkSessionHub();
        using var publisher = new MediaLinkStatePublisher(
            upstreamCoordinator, upstreamHub, timelineMinIntervalMs: () => 0);
        publisher.Start();
        var upstreamServer = new MediaLinkServer(
            upstreamHub, s => publisher.PublishSnapshotAsync(s), () => "up-tok", coordinator: upstreamCoordinator);
        await upstreamServer.StartAsync("127.0.0.1", 0);
        var upstreamPort = int.Parse(upstreamServer.Endpoint!.Split(':')[2].Split('/')[0]);

        upstreamMedia.Raise(
            new MediaInfo("upstream-app", "RelaySong", "RelayArtist", null,
                TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(3),
                new MediaPlaybackInfo(MediaPlaybackState.Paused), null, null),
            MediaInfoChangeKind.MediaProperties);

        // 中继节点：既向上游收，又对下游开服务端。两个宿主服务共用一份设置，与插件里一致。
        var relaySettings = new PluginSettings
        {
            MediaLinkMediaSourceMode = MediaLinkMediaSourceMode.ExternalPreferred,
            MediaLinkIsEnabled = true,
            MediaLinkListenAddress = "127.0.0.1",
            MediaLinkPort = 0,
            MediaLinkToken = "relay-tok",
            MediaLinkUpstreamIsEnabled = true,
            MediaLinkUpstreamEndpoint = $"127.0.0.1:{upstreamPort}",
            MediaLinkUpstreamToken = "up-tok"
        };
        var relayStore = new MediaLinkInjectionStore();
        var relayMedia = new E2EFakeMediaService();
        using var relayCoordinator = new MediaSourceCoordinator(
            relayMedia, lyrics, relayStore, () => relaySettings);
        using var relayServer = new MediaLinkHostedService(
            relayMedia, lyrics, relayStore, relayCoordinator,
            new MediaPlatformProviderResolver([], NullLogger<MediaPlatformProviderResolver>.Instance),
            () => relaySettings,
            externalMediaEffectiveAccessor: () => relayCoordinator.IsExternalMediaEffective);
        using var relayUpstream = new MediaLinkUpstreamHostedService(
            relayStore, relayCoordinator, () => relaySettings,
            downstreamAudioDemandAccessor: () => relayServer.HasDownstreamAudioDemand);

        // 用产品里的那份接线，而不是在测试里另抄一份——抄一份就只能证明抄得对。
        MediaLinkAudioWiring.Connect(relayUpstream, relayServer, relayCoordinator);

        // 边沿计数。自激循环（重算→通知→重算）不会报错，只会把 CPU 烧掉，
        // 而它与「正确地只通知一次」在最终状态上完全一样，只有计数分得开。
        var demandChanges = 0;
        relayServer.DownstreamAudioDemandChanged += (_, _) => Interlocked.Increment(ref demandChanges);

        await StartBoundedAsync(relayServer);
        await StartBoundedAsync(relayUpstream);

        // 前置一：上游那首歌已成为中继节点的生效媒体。仲裁的「生效媒体来自上游」要成立，
        // 且这一步之后媒体不再变化。
        await WaitUntilAsync(() => relayCoordinator.IsExternalMediaEffective);
        Assert.True(relayCoordinator.IsExternalMediaEffective, "前置不成立：生效媒体应判为来自上游");

        // 前置二：向上游的握手已走完，但此刻没有任何音频需求，故不该订阅 audio。
        // 不钉这一条，「一开始就订阅了」会冒充成功。
        await WaitUntilAsync(() => upstreamHub.Sessions.Count == 1);
        var upstreamSession = Assert.Single(upstreamHub.Sessions);
        await WaitUntilAsync(() => upstreamSession.IsSubscribedTo(MediaLinkProtocol.ChannelMedia));
        Assert.True(upstreamSession.IsSubscribedTo(MediaLinkProtocol.ChannelMedia), "握手没走到 subscribe");
        Assert.False(relayServer.HasDownstreamAudioDemand);
        Assert.False(upstreamSession.IsSubscribedToAudio, "前置不成立：没有下游要音频时不该订阅 audio");

        // 唯一的状态变化：一个下游会话接上来并索要音频。
        using var downstream = new ClientWebSocket();
        await ConnectBoundedAsync(downstream, new Uri(relayServer.Endpoint!));
        await ReceiveJsonAsync(downstream); // hello
        await SendJsonAsync(downstream, new
        {
            type = MediaLinkProtocol.TypeAuth, id = "a1", v = 1, ts = NowMs(),
            payload = new { token = relaySettings.MediaLinkToken }
        });
        await ReceiveUntilTypeAsync(downstream, MediaLinkProtocol.TypeAuthOk);
        await SendJsonAsync(downstream, new
        {
            type = MediaLinkProtocol.TypeSubscribe, id = "s1", v = 1, ts = NowMs(),
            payload = new { channels = new[] { MediaLinkProtocol.ChannelAudio } }
        });
        await ReceiveUntilTypeAsync(downstream, MediaLinkProtocol.TypeSubscribeOk);
        await SendJsonAsync(downstream, new
        {
            type = MediaLinkProtocol.TypeAudioPlayStart, id = "ap1", v = 1, ts = NowMs()
        });

        // 应答在重算之后才发出，故收到它即表示服务端已经算完并通知过了。
        await ReceiveUntilTypeAsync(downstream, MediaLinkProtocol.TypeOk);
        Assert.True(relayServer.HasDownstreamAudioDemand, "地基不成立：下游的音频需求没被服务端看见");

        // 验收点：这条需求必须一路传到上游。上游不订阅就一帧都不发，转发无从谈起。
        await WaitUntilAsync(() => upstreamSession.IsSubscribedToAudio);
        Assert.True(
            upstreamSession.IsSubscribedToAudio,
            $"下游要音频却没能拉起上游订阅，上游会话当前频道：[{string.Join(", ", upstreamSession.Channels)}]");

        // 需求只翻转过一次，通知就该只有一次。大于一说明重算与通知互相触发了起来。
        Assert.Equal(1, Volatile.Read(ref demandChanges));

        await CloseBoundedAsync(downstream);
        await StopBoundedAsync(relayUpstream);
        await StopBoundedAsync(relayServer);
        await upstreamServer.StopAsync();
    }

    [Fact]
    public async Task EffectiveMediaTurningUpstreamAfterConnect_SubscribesAudio()
    {
        // 与「握手完成即订阅」互补的另一条触发路径：连接先建立，生效媒体来源之后才变成上游。
        // 握手那一刻仲裁的「生效媒体来自上游」为假，连接态回调补不到；
        // 真正补上它的是接线里「生效媒体变化 → 重算向上游的订阅」这条边，
        // 也正是把重算方法提为 public 的理由。断了同样不报错，只会静默收不到音频。
        var lyrics = new LyricsSearchService([], [], () => new LyricsSourceSettings());

        var upstreamMedia = new E2EFakeMediaService();
        var upstreamSettings = new PluginSettings
        {
            MediaLinkMediaSourceMode = MediaLinkMediaSourceMode.PlatformOnly,
            MediaLinkPushUsesEffective = true
        };
        using var upstreamCoordinator = new MediaSourceCoordinator(
            upstreamMedia, lyrics, new MediaLinkInjectionStore(), () => upstreamSettings);
        var hub = new MediaLinkSessionHub();
        using var publisher = new MediaLinkStatePublisher(
            upstreamCoordinator, hub, timelineMinIntervalMs: () => 0);
        publisher.Start();
        var server = new MediaLinkServer(
            hub, s => publisher.PublishSnapshotAsync(s), () => "up-tok", coordinator: upstreamCoordinator);
        await server.StartAsync("127.0.0.1", 0);
        var port = int.Parse(server.Endpoint!.Split(':')[2].Split('/')[0]);

        // 下游注入存储此刻是空的：握手时生效媒体判不成来自上游，仲裁必为假。
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

        // 「要不要」的三个来源里取转发这一路：另两路要真频谱组件或真播放设备。
        using var upstreamService = new MediaLinkUpstreamHostedService(
            downstreamStore, downstreamCoordinator, () => downstreamSettings,
            downstreamAudioDemandAccessor: () => true);

        // 本机服务端只为把产品里的那份接线原样用上；本用例不启动它，
        // 未启动的服务端对重算是空操作，不会替生效媒体那条边做任何事。
        using var localServer = new MediaLinkHostedService(
            downstreamMedia, lyrics, downstreamStore, downstreamCoordinator,
            new MediaPlatformProviderResolver([], NullLogger<MediaPlatformProviderResolver>.Instance),
            () => downstreamSettings);
        MediaLinkAudioWiring.Connect(upstreamService, localServer, downstreamCoordinator);

        await StartBoundedAsync(upstreamService);

        // 前置：握手已走完，而生效媒体还不是上游的，故此刻不该订阅 audio。
        await WaitUntilAsync(() => hub.Sessions.Count == 1);
        var session = Assert.Single(hub.Sessions);
        await WaitUntilAsync(() => session.IsSubscribedTo(MediaLinkProtocol.ChannelMedia));
        Assert.True(session.IsSubscribedTo(MediaLinkProtocol.ChannelMedia), "握手没走到 subscribe");
        Assert.False(downstreamCoordinator.IsExternalMediaEffective, "前置不成立：此刻生效媒体不该来自上游");
        Assert.False(session.IsSubscribedToAudio, "前置不成立：仲裁为假时不该订阅 audio");

        // 上游开始放歌，媒体经推送落进下游注入存储，生效媒体来源随之翻成上游。
        upstreamMedia.Raise(
            new MediaInfo("upstream-app", "LateArrivalSong", "LateArrivalArtist", null,
                TimeSpan.FromSeconds(8), TimeSpan.FromMinutes(4),
                new MediaPlaybackInfo(MediaPlaybackState.Playing), null, null),
            MediaInfoChangeKind.MediaProperties);

        await WaitUntilAsync(() => downstreamCoordinator.IsExternalMediaEffective);
        Assert.True(downstreamCoordinator.IsExternalMediaEffective, "地基不成立：上游媒体没能成为生效媒体");

        // 验收点本身。
        await WaitUntilAsync(() => session.IsSubscribedToAudio);
        Assert.True(
            session.IsSubscribedToAudio,
            $"生效媒体转为上游后仍未订阅 audio，会话当前频道：[{string.Join(", ", session.Channels)}]");

        await StopBoundedAsync(upstreamService);
        await server.StopAsync();
    }

    [Fact]
    public async Task Thumbnail_FlowsFromServerToInjectionStore_OverRealSocket()
    {
        // 封面走完整条真实链路：服务端 media.updated → 内置客户端问 thumbnail.get
        // → 服务端回 thumbnail → 写进注入存储。此前客户端从不发那个请求，
        // 接收端的组件因此永远拿不到封面。
        //
        // 曲目声明有封面但加载返回 null：服务端编码真图需要 IPlatformRenderInterface，
        // 测试进程没有。这个组合让 hasThumbnail 为真（客户端因此会问），
        // 而服务端编码时拿到 null 回空数据——往返完整发生，只是载荷为空。
        var upstreamMedia = new E2EFakeMediaService();
        var upstreamLyrics = new LyricsSearchService([], [], () => new LyricsSourceSettings());
        var upstreamStore = new MediaLinkInjectionStore();
        var upstreamSettings = new PluginSettings
        {
            MediaLinkMediaSourceMode = MediaLinkMediaSourceMode.PlatformOnly,
            MediaLinkPushUsesEffective = true
        };
        using var upstreamCoordinator = new MediaSourceCoordinator(
            upstreamMedia, upstreamLyrics, upstreamStore, () => upstreamSettings);

        var hub = new MediaLinkSessionHub();
        using var publisher = new MediaLinkStatePublisher(
            upstreamCoordinator, hub, timelineMinIntervalMs: () => 0);
        publisher.Start();
        const string token = "thumb-e2e-token";
        var server = new MediaLinkServer(
            hub,
            session => publisher.PublishSnapshotAsync(session),
            () => token,
            coordinator: upstreamCoordinator,
            logger: null);
        await server.StartAsync("127.0.0.1", 0);
        var port = int.Parse(server.Endpoint!.Split(':')[2].Split('/')[0]);

        // 上游先有曲目，客户端连上即会收到一条 media.updated。
        upstreamMedia.Raise(
            new MediaInfo(
                "upstream.exe", "E2E Song", "E2E Artist", "Album",
                TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(3),
                new MediaPlaybackInfo(MediaPlaybackState.Playing, 1.0),
                Thumbnail: null,
                ThumbnailSource: new MediaThumbnail(
                    (_, _) => Task.FromResult<Avalonia.Media.Imaging.Bitmap?>(null))),
            MediaInfoChangeKind.CurrentSession);

        // 接收侧：真正的内置客户端 + 真正的上游宿主服务。
        var downMedia = new E2EFakeMediaService();
        var downLyrics = new LyricsSearchService([], [], () => new LyricsSourceSettings());
        var downStore = new MediaLinkInjectionStore();
        var downSettings = new PluginSettings
        {
            MediaLinkMediaSourceMode = MediaLinkMediaSourceMode.ExternalPreferred,
            MediaLinkUpstreamIsEnabled = true,
            MediaLinkUpstreamEndpoint = $"127.0.0.1:{port}",
            MediaLinkUpstreamToken = token
        };
        using var downCoordinator = new MediaSourceCoordinator(
            downMedia, downLyrics, downStore, () => downSettings);
        using var upstreamService = new MediaLinkUpstreamHostedService(
            downStore, downCoordinator, () => downSettings);

        try
        {
            await upstreamService.StartAsync(CancellationToken.None);

            // 先等曲目注入，再等封面解析——两者是两条消息，顺序即链路的形状。
            await WaitUntilAsync(() => downStore.HasExternalMedia);
            Assert.True(downStore.HasExternalMedia, "上游媒体未注入，封面链路无从开始");

            await WaitUntilAsync(() => downStore.ResolvedThumbnailToken is not null);

            // token 相等即证明：请求带对了曲目、服务端按同一曲目作答、接收侧认下了它。
            // 三者中任一环失配，这个值都会留在 null。
            var expected = MediaLinkDtoMapper.ComputeTrackToken(
                "upstream.exe", "E2E Song", "E2E Artist", "Album");
            Assert.Equal(expected, downStore.ResolvedThumbnailToken);
        }
        finally
        {
            await upstreamService.StopAsync(CancellationToken.None);
            await server.StopAsync();
        }
    }

    /// <summary>
    /// 读到指定 type 的报文为止。中途可能夹着快照与事件推送，
    /// 不能假定下一条就是刚发出去那条请求的应答。
    /// </summary>
    [Fact]
    public async Task AudioClock_AnswersWithFourTimestampsInOrder()
    {
        var hub = new MediaLinkSessionHub();
        var server = new MediaLinkServer(hub, _ => Task.CompletedTask, () => "tok");
        await server.StartAsync("127.0.0.1", 0);
        var actualPort = int.Parse(server.Endpoint!.Split(':')[2].Split('/')[0]);

        using var client = new ClientWebSocket();
        await ConnectBoundedAsync(client, new Uri($"ws://127.0.0.1:{actualPort}/v1/ws"));

        var hello = await ReceiveJsonAsync(client);
        var capabilities = hello.GetProperty("payload").GetProperty("capabilities")
            .EnumerateArray().Select(c => c.GetString()).ToList();
        Assert.Contains(MediaLinkProtocol.CapabilityAudioClock, capabilities);

        await SendJsonAsync(client, new { type = "auth", id = "a1", v = 1, ts = NowMs(), payload = new { token = "tok" } });
        await ReceiveJsonAsync(client);

        // 本判据能断言 t1 <= t2 <= t3 <= t4，只因为测试里客户端与服务端同进程，
        // 两侧读的是同一个 MonotonicClock。真实跨机时这四个数分属两个时钟，
        // 只有 t1 <= t4 与 t2 <= t3 各自成立，跨侧比较无意义。
        var t1 = MonotonicClock.Now100Ns();
        await SendJsonAsync(client, new
        {
            type = "audio.clock", id = "c1", v = 1, ts = NowMs(), payload = new { t1 }
        });
        var reply = await ReceiveJsonAsync(client);
        var t4 = MonotonicClock.Now100Ns();

        Assert.Equal(MediaLinkProtocol.TypeAudioClock, reply.GetProperty("type").GetString());
        Assert.Equal("c1", reply.GetProperty("id").GetString());
        var payload = reply.GetProperty("payload");
        Assert.Equal(MediaLinkProtocol.TypeAudioClock, payload.GetProperty("for").GetString());
        Assert.Equal(t1, payload.GetProperty("t1").GetInt64());

        var t2 = payload.GetProperty("t2").GetInt64();
        var t3 = payload.GetProperty("t3").GetInt64();
        Assert.True(t1 <= t2, $"t2 {t2} 早于 t1 {t1}");
        Assert.True(t2 <= t3, $"t3 {t3} 早于 t2 {t2}");
        Assert.True(t3 <= t4, $"t4 {t4} 早于 t3 {t3}");

        await CloseBoundedAsync(client);
        await server.StopAsync();
    }

    [Fact]
    public async Task AudioClock_WithoutT1_IsRejectedRatherThanAnsweredWithZero()
    {
        var hub = new MediaLinkSessionHub();
        var server = new MediaLinkServer(hub, _ => Task.CompletedTask, () => "tok");
        await server.StartAsync("127.0.0.1", 0);
        var actualPort = int.Parse(server.Endpoint!.Split(':')[2].Split('/')[0]);

        using var client = new ClientWebSocket();
        await ConnectBoundedAsync(client, new Uri($"ws://127.0.0.1:{actualPort}/v1/ws"));
        await ReceiveJsonAsync(client);
        await SendJsonAsync(client, new { type = "auth", id = "a1", v = 1, ts = NowMs(), payload = new { token = "tok" } });
        await ReceiveJsonAsync(client);

        // 回一个 t1 为 0 的应答会让客户端算出一个巨大的 offset 并当真，
        // 那比报错难查得多——错值看起来是合法的。
        await SendJsonAsync(client, new { type = "audio.clock", id = "c1", v = 1, ts = NowMs(), payload = new { } });
        var reply = await ReceiveJsonAsync(client);

        Assert.Equal(MediaLinkProtocol.TypeError, reply.GetProperty("type").GetString());
        Assert.Equal(
            MediaLinkProtocol.ErrorBadRequest,
            reply.GetProperty("payload").GetProperty("code").GetString());

        await CloseBoundedAsync(client);
        await server.StopAsync();
    }

    private static async Task<JsonElement> ReceiveUntilTypeAsync(WebSocket ws, string type)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var message = await ReceiveJsonAsync(ws, timeoutMs: 5000);
            if (message.GetProperty("type").GetString() == type)
            {
                return message;
            }
        }

        throw new TimeoutException($"未在限期内收到 type={type} 的报文");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
    }

    /// <summary>
    /// 以下三个 helper 收口本类里所有传给真实 socket 与真实宿主服务的 token。
    ///
    /// 规则：凡传给真实 socket 的 I/O 或真实 HostedService 启停的 token，
    /// 不得为 CancellationToken.None。它的失效形态是整个测试进程无输出挂起——最难查的
    /// 一种，且在 CI 上与「跑得慢」无从区分。放进 try 里也挡不住：挂起不抛异常。
    ///
    /// 传给假对象的 token 不在射程内，那些不会阻塞，逐个换只会让代码变吵。
    /// </summary>
    private static async Task ConnectBoundedAsync(ClientWebSocket socket, Uri uri, int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        await socket.ConnectAsync(uri, cts.Token);
    }

    /// <summary>带界的正常关闭。清理路径，失败即忽略，但不得无界。</summary>
    private static async Task CloseBoundedAsync(WebSocket socket, int timeoutMs = 5000)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cts.Token);
        }
        catch
        {
            // 对端已死或不回关闭握手：测试的清理不因此失败
        }
    }

    /// <summary>带界的宿主服务启停。两者内部都要抢生命周期锁，锁被占住时无界会挂。</summary>
    private static async Task StartBoundedAsync(IHostedService service, int timeoutMs = 10_000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        await service.StartAsync(cts.Token);
    }

    private static async Task StopBoundedAsync(IHostedService service, int timeoutMs = 10_000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        await service.StopAsync(cts.Token);
    }

    /// <summary>
    /// 生产适配器版本。它包的是同一个 ClientWebSocket，走的是同一个真 socket，
    /// 故同样在规则射程内——收发也算 I/O，不只是连接。
    /// </summary>
    private static async Task ConnectBoundedAsync(IMediaLinkClientSocket socket, Uri uri, int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        await socket.ConnectAsync(uri, cts.Token);
    }

    private static async Task<MediaLinkSocketMessage> ReceiveBoundedAsync(
        IMediaLinkClientSocket socket, int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        return await socket.ReceiveAsync(cts.Token);
    }

    private static async Task SendBoundedAsync(
        IMediaLinkClientSocket socket, string text, int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        await socket.SendTextAsync(text, cts.Token);
    }

    [Fact]
    public async Task UpstreamTimelineUpdates_DoNotLookLikeTrackChangesDownstream()
    {
        // 接收端歌词不断刷新的判据。上游播放时每 200ms 推一条位置更新，接收端若把它们
        // 当成换歌，歌词组件就会每 200ms 走一次整条重载路径，把高亮行与间奏动画清零。
        //
        // 验收落在「接收端看到的变更种类」而非最终状态：位置一直在前进，
        // 修好前与修好后的最终状态完全一样，只有种类分得开。
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
            hub, s => publisher.PublishSnapshotAsync(s), () => "tl-tok", coordinator: upstreamCoordinator);
        await server.StartAsync("127.0.0.1", 0);
        var port = int.Parse(server.Endpoint!.Split(':')[2].Split('/')[0]);

        var downstreamSettings = new PluginSettings
        {
            MediaLinkMediaSourceMode = MediaLinkMediaSourceMode.ExternalPreferred,
            MediaLinkUpstreamIsEnabled = true,
            MediaLinkUpstreamEndpoint = $"127.0.0.1:{port}",
            MediaLinkUpstreamToken = "tl-tok"
        };
        var downstreamStore = new MediaLinkInjectionStore();
        using var downstreamCoordinator = new MediaSourceCoordinator(
            new E2EFakeMediaService(), lyrics, downstreamStore, () => downstreamSettings);
        using var upstreamService = new MediaLinkUpstreamHostedService(
            downstreamStore, downstreamCoordinator, () => downstreamSettings);

        await StartBoundedAsync(upstreamService);
        await WaitUntilAsync(() => upstreamService.IsConnected);

        // 先换歌。这一条必须被接收端认成会话级变更，否则歌词永远不刷新——
        // 那是本修复的反向失效，比刷太多更坏，故与刷太多同用一条判据钉住。
        var track = new MediaInfo("upstream-app", "TimelineSong", "TimelineArtist", null,
            TimeSpan.Zero, TimeSpan.FromMinutes(4),
            new MediaPlaybackInfo(MediaPlaybackState.Playing), null, null);
        upstreamMedia.Raise(track, MediaInfoChangeKind.CurrentSession);
        await WaitUntilAsync(() => downstreamStore.GetMediaSnapshot()?.Title == "TimelineSong");

        var sessionLevel = 0;
        var timeline = 0;
        downstreamCoordinator.EffectiveMediaChanged += (_, e) =>
        {
            if (e.ChangeKind is MediaInfoChangeKind.CurrentSession or MediaInfoChangeKind.MediaProperties)
            {
                Interlocked.Increment(ref sessionLevel);
            }
            else if (e.ChangeKind == MediaInfoChangeKind.Timeline)
            {
                Interlocked.Increment(ref timeline);
            }
        };

        // 同一首歌走时间线前进五次。
        for (var i = 1; i <= 5; i++)
        {
            upstreamMedia.Raise(
                track with { Position = TimeSpan.FromSeconds(i) },
                MediaInfoChangeKind.Timeline);
        }

        await WaitUntilAsync(() => Volatile.Read(ref timeline) >= 5);

        Assert.True(
            Volatile.Read(ref timeline) >= 5,
            $"地基不成立：时间线更新没走到接收端，只收到 {Volatile.Read(ref timeline)} 条");
        Assert.Equal(0, Volatile.Read(ref sessionLevel));

        await StopBoundedAsync(upstreamService);
        await server.StopAsync();
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
        await ConnectBoundedAsync(client, new Uri($"ws://127.0.0.1:{port}/v1/ws"));
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

        await CloseBoundedAsync(client);
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
        await ConnectBoundedAsync(client, new Uri($"ws://127.0.0.1:{port}/v1/ws"));
        await ReceiveJsonAsync(client); // hello
        await SendJsonAsync(client, new { type = "auth", id = "a1", v = 1, ts = NowMs(), payload = new { token = "tok" } });
        await ReceiveJsonAsync(client); // auth_ok

        var pcm = new byte[96 * 1024];
        Random.Shared.NextBytes(pcm);
        var frame = MediaLinkAudioFrame.Encode(
            new MediaLinkAudioFrameHeader(11, 22, 33, 44, "inbound-track", MediaLinkAudioFrameFlags.Silent),
            pcm);

        using var sendCts = new CancellationTokenSource(5000);
        await client.SendAsync(frame, WebSocketMessageType.Binary, endOfMessage: true, sendCts.Token);

        await WaitUntilAsync(() => receivedPcm is not null);
        Assert.NotNull(receivedPcm);
        Assert.Equal("inbound-track", receivedHeader!.Value.TrackToken);
        Assert.Equal(44u, receivedHeader.Value.Seq);
        Assert.Equal(pcm, receivedPcm);

        await CloseBoundedAsync(client);
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
        await ConnectBoundedAsync(socket, new Uri($"ws://127.0.0.1:{port}/v1/ws"));
        await ReceiveBoundedAsync(socket); // hello
        await SendBoundedAsync(
            socket,
            MediaLinkMessageSerializer.Serialize(MediaLinkMessageSerializer.Create(
                MediaLinkProtocol.TypeAuth, new MediaLinkAuthPayload { Token = "tok" }, id: "a1")));
        await ReceiveBoundedAsync(socket); // auth_ok
        await SendBoundedAsync(
            socket,
            MediaLinkMessageSerializer.Serialize(MediaLinkMessageSerializer.Create(
                MediaLinkProtocol.TypeSubscribe,
                new MediaLinkSubscribePayload { Channels = [MediaLinkProtocol.ChannelAudio] },
                id: "s1")));
        await ReceiveBoundedAsync(socket); // subscribe_ok

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
        await ConnectBoundedAsync(client, new Uri($"ws://127.0.0.1:{port}/v1/ws"));
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

        // 收集直到出现第一条描述了曲目的事件，或到 deadline。
        //
        // 门槛是「至少一条」而不是「至少两条」，这一改动是本条测试从随负载随机红
        // 转为稳定的全部原因，故理由必须写清：
        //
        // 事件条数是发布侧合并行为的函数，不是被测契约。MediaLinkStatePublisher
        // 是快照式发布，把 30ms 内的连续切歌合并成一条是它允许的行为，负载高时
        // 合并得更多。要求两条等于要求「发布侧不许合并」——那从来不是承诺。
        //
        // 原门槛的顾虑是对的：滤完一条不剩时下面的 foreach 空转，测试照样绿，
        // 那是「被测代码什么都不做也能通过」的假绿。但用一个会随机不成立的条数
        // 去防它，是把真问题挡在了一个随机失败的门后面。改由「等到至少一条，
        // 超时即红」来防——超时红是真红，说明发布侧一条都没发。
        var received = new List<JsonElement>();
        var trackEvents = new List<JsonElement>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);

        while (DateTime.UtcNow < deadline)
        {
            JsonElement evt;
            try
            {
                evt = await ReceiveJsonAsync(client, timeoutMs: 500);
            }
            catch (OperationCanceledException)
            {
                // 一次读超时不代表结束：已经收到目标就收尾，否则继续等到 deadline。
                if (trackEvents.Count > 0)
                {
                    break;
                }

                continue;
            }
            catch (WebSocketException)
            {
                break;
            }

            received.Add(evt);

            if (!evt.TryGetProperty("name", out var name)
                || name.GetString() != MediaLinkProtocol.EventMediaUpdated)
            {
                continue;
            }

            // 订阅时推送的快照若赶在首次切歌之前取样，此刻还没有任何媒体，会发出一条
            // 没有 payload 的 media.updated，语义是「当前无播放」。它本就不该带
            // trackToken，必须先滤掉。
            if (evt.TryGetProperty("payload", out var payload)
                && payload.ValueKind == JsonValueKind.Object
                && payload.TryGetProperty("title", out _))
            {
                trackEvents.Add(evt);
            }
        }

        var mediaEvents = received
            .Where(e => e.TryGetProperty("name", out var n) && n.GetString() == MediaLinkProtocol.EventMediaUpdated)
            .ToList();

        Assert.True(
            trackEvents.Count >= 1,
            $"10 次切歌后没有任何 media.updated 描述了曲目。media.updated: {mediaEvents.Count}, 收到总数: {received.Count}");

        foreach (var evt in trackEvents)
        {
            // 过滤已保证 payload 存在，这里取用不会抛 KeyNotFoundException。
            Assert.True(evt.GetProperty("payload").TryGetProperty("trackToken", out _),
                $"media.updated described a track but carried no trackToken: {evt}");
        }

        await CloseBoundedAsync(client);
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
        await ConnectBoundedAsync(client, new Uri($"ws://127.0.0.1:{port}/v1/ws"));
        var hello = await ReceiveJsonAsync(client);
        Assert.Equal(MediaLinkProtocol.EventServerHello, hello.GetProperty("name").GetString());
        var epoch = hello.GetProperty("payload").GetProperty("sessionEpoch").GetInt64();
        await CloseBoundedAsync(client);
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
        await ConnectBoundedAsync(client, new Uri($"ws://127.0.0.1:{port}/v1/ws"));
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

        await CloseBoundedAsync(client);
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
            await ConnectBoundedAsync(bad, new Uri($"ws://127.0.0.1:{port}/v1/ws"));
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

    [Fact]
    public async Task RelayedAudioFrame_ThroughTheRegisteredGraph_ArrivesAtTheThirdHopByteIdentical()
    {
        // 三跳的合成。每一跳单独都已有判据：A→B 的字节相等
        // （AudioFrame_OverRealWebSocket_ArrivesByteIdentical）、中继接缝的原样转发
        // （MediaLinkAudioForwardingTests.AcceptedFrame_IsForwardedByteForByte）、
        // 需求向上游传导（DownstreamAudioDemand_AloneIsEnough_ToPullUpstreamSubscription）。
        //
        // 没人看守的是注册图里那个 audioForwarder lambda 与 BroadcastAudioFrameAsync 的接合：
        // 既有的转发链测试手搭中继节点时根本没传 audioForwarder。那条边断了不会报错，
        // 只会让 C 永远收不到音频，而 A 与 B 之间一切正常。
        //
        // 故 B 由产品的注册图建起来而不是在测试里手搭——手搭只能证明手搭得对。
        var lyrics = new LyricsSearchService([], [], () => new LyricsSourceSettings());

        // A：最初的采集端。要走真实的媒体推送，否则 B 判不出「生效媒体来自上游」，
        // 而那是订阅上游音频的前提之一。
        var originMedia = new E2EFakeMediaService();
        var originSettings = new PluginSettings
        {
            MediaLinkMediaSourceMode = MediaLinkMediaSourceMode.PlatformOnly,
            MediaLinkPushUsesEffective = true
        };
        using var originCoordinator = new MediaSourceCoordinator(
            originMedia, lyrics, new MediaLinkInjectionStore(), () => originSettings);
        var originHub = new MediaLinkSessionHub();
        using var originPublisher = new MediaLinkStatePublisher(
            originCoordinator, originHub, timelineMinIntervalMs: () => 0);
        originPublisher.Start();
        var originServer = new MediaLinkServer(
            originHub, s => originPublisher.PublishSnapshotAsync(s), () => "origin-tok",
            coordinator: originCoordinator);
        await originServer.StartAsync("127.0.0.1", 0);
        var originPort = int.Parse(originServer.Endpoint!.Split(':')[2].Split('/')[0]);

        originMedia.Raise(
            new MediaInfo("origin-app", "RelayHopSong", "RelayHopArtist", null,
                TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(4),
                new MediaPlaybackInfo(MediaPlaybackState.Playing), null, null),
            MediaInfoChangeKind.MediaProperties);

        // B：中继节点，整张对象图来自 AddMediaLink。
        var relaySettings = new PluginSettings
        {
            MediaLinkMediaSourceMode = MediaLinkMediaSourceMode.ExternalPreferred,
            MediaLinkIsEnabled = true,
            MediaLinkListenAddress = "127.0.0.1",
            MediaLinkPort = 0,
            MediaLinkToken = "relay-tok",
            MediaLinkUpstreamIsEnabled = true,
            MediaLinkUpstreamEndpoint = $"127.0.0.1:{originPort}",
            MediaLinkUpstreamToken = "origin-tok"
        };
        var services = new ServiceCollection();
        services.AddSingleton<IMediaService>(new E2EFakeMediaService());
        services.AddSingleton(new LyricsSearchService([], [], () => new LyricsSourceSettings()));
        services.AddSingleton(new MediaPlatformProviderResolver(
            [], NullLogger<MediaPlatformProviderResolver>.Instance));
        services.AddMediaLink(() => relaySettings);
        await using var provider = services.BuildServiceProvider();

        var relayServer = provider.GetRequiredService<MediaLinkHostedService>();
        var relayUpstream = provider.GetRequiredService<MediaLinkUpstreamHostedService>();
        await StartBoundedAsync(relayServer);
        await StartBoundedAsync(relayUpstream);

        await WaitUntilAsync(() =>
            provider.GetRequiredService<MediaSourceCoordinator>().IsExternalMediaEffective);
        await WaitUntilAsync(() => originHub.Sessions.Count == 1);
        var originSession = Assert.Single(originHub.Sessions);
        await WaitUntilAsync(() => originSession.IsSubscribedTo(MediaLinkProtocol.ChannelMedia));

        // 地基：此刻没有任何下游要音频，A 就不该被订阅 audio。一帧不发，转发无从谈起，
        // 而 A 白采集、白传约 192KB/s。不钉这一条，「一开始就在转」会冒充成功。
        Assert.False(originSession.IsSubscribedToAudio, "前置不成立：无下游需求时不该订阅 audio");

        // C：第三跳。用裸 socket 而非 MediaLinkClient——要验的是线上字节。
        using var thirdHop = new ClientWebSocket();
        await ConnectBoundedAsync(thirdHop, new Uri(relayServer.Endpoint!));
        await ReceiveJsonAsync(thirdHop); // hello
        await SendJsonAsync(thirdHop, new
        {
            type = MediaLinkProtocol.TypeAuth, id = "a1", v = 1, ts = NowMs(),
            payload = new { token = relaySettings.MediaLinkToken }
        });
        await ReceiveUntilTypeAsync(thirdHop, MediaLinkProtocol.TypeAuthOk);
        await SendJsonAsync(thirdHop, new
        {
            type = MediaLinkProtocol.TypeSubscribe, id = "s1", v = 1, ts = NowMs(),
            payload = new { channels = new[] { MediaLinkProtocol.ChannelAudio } }
        });
        await ReceiveUntilTypeAsync(thirdHop, MediaLinkProtocol.TypeSubscribeOk);
        await SendJsonAsync(thirdHop, new
        {
            type = MediaLinkProtocol.TypeAudioPlayStart, id = "ap1", v = 1, ts = NowMs()
        });
        await ReceiveUntilTypeAsync(thirdHop, MediaLinkProtocol.TypeOk);

        await WaitUntilAsync(() => originSession.IsSubscribedToAudio);
        Assert.True(originSession.IsSubscribedToAudio, "C 要音频却没能一路拉起 B 对 A 的订阅");

        // B 的接收侧按 trackToken 丢弃过期曲目的 PCM，故这一帧必须带 B 当前认定的 token。
        var snapshot = provider.GetRequiredService<MediaLinkInjectionStore>().GetMediaSnapshot();
        Assert.NotNull(snapshot);
        var trackToken = MediaLinkDtoMapper.ComputeTrackToken(
            snapshot!.SourceApp, snapshot.Title, snapshot.Artist, snapshot.AlbumTitle);

        var pcm = new byte[4096];
        Random.Shared.NextBytes(pcm);
        var originFrame = MediaLinkAudioFrame.Encode(
            new MediaLinkAudioFrameHeader(1234, 5678, 9012, 77, trackToken, MediaLinkAudioFrameFlags.None),
            pcm);
        await originSession.EnqueueAudioAsync(originFrame, CancellationToken.None);

        // 验收点：C 收到的字节与 A 发出的逐一相等，三个时间戳与 seq 都在。
        // 中继改写它们不会报错，只会把 A 那一跳的时间信息抹掉，
        // 让 C 算出的传输延迟只覆盖最后一跳。
        var arrived = await ReceiveBinaryAsync(thirdHop);
        Assert.Equal(originFrame, arrived);

        static (MediaLinkAudioFrameHeader Header, byte[] Pcm) Decode(byte[] frame)
        {
            Assert.True(MediaLinkAudioFrame.TryDecode(frame, out var header, out var pcm, out _));
            return (header, pcm.ToArray());
        }

        var (header, decodedPcm) = Decode(arrived);
        Assert.Equal(trackToken, header.TrackToken);
        Assert.Equal(77u, header.Seq);
        Assert.Equal(1234, header.StartPositionMs);
        Assert.Equal(5678, header.CapturedAtMs);
        Assert.Equal(9012, header.ServerTimeMs);
        Assert.Equal(pcm, decodedPcm);

        await CloseBoundedAsync(thirdHop);
        await StopBoundedAsync(relayUpstream);
        await StopBoundedAsync(relayServer);
        await originServer.StopAsync();
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
        // 本类里每条用例都经这里发消息，故它是射程内最要紧的一处：
        // 无界的话，任何一次对端不读的发送都会让整个测试进程静默挂住。
        using var cts = new CancellationTokenSource(5000);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cts.Token);
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