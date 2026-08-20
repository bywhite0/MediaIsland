using MediaIsland.Models;
using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Lyrics.Models;
using MediaIsland.Services.Media;
using MediaIsland.Services.Media.Platform;
using MediaIsland.Services.MediaLink;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net.Sockets;
using System.Net.WebSockets;
using Xunit;

namespace MediaIsland.Tests.MediaLink;

/// <summary>
/// 设置变更的生效方式（规格 §12「设置层」）。
///
/// 端口是进程级共享资源，且这些用例都要起真实 listener，故整类串行。
/// </summary>
[Collection(nameof(MediaLinkSettingsTests))]
[CollectionDefinition(nameof(MediaLinkSettingsTests), DisableParallelization = true)]
public class MediaLinkSettingsTests
{
    /// <summary>
    /// 防抖窗口 500ms。重建还要等停服完成，而停服会对每个在线会话发 1001 GoingAway
    /// 并最多等 1s 关闭握手，故留足余量避免误判为启动失败。
    /// </summary>
    private static readonly TimeSpan DebounceWait = TimeSpan.FromMilliseconds(1500);

    /// <summary>涉及在线会话被踢的用例需额外覆盖 GoingAway 的等待时间。</summary>
    private static readonly TimeSpan RebuildWithSessionsWait = TimeSpan.FromSeconds(4);

    [Fact]
    public async Task EnabledWithEmptyToken_GeneratesTokenAndStarts()
    {
        var settings = NewSettings();
        settings.MediaLinkToken = string.Empty;
        using var host = NewHost(settings);

        await StartBoundedAsync(host);

        Assert.True(host.IsRunning);
        Assert.False(string.IsNullOrWhiteSpace(settings.MediaLinkToken));
        Assert.Null(host.LastError);
    }

    [Fact]
    public async Task InvalidListenAddress_DoesNotStart_AndReportsError()
    {
        var settings = NewSettings();
        settings.MediaLinkListenAddress = "not-an-ip";
        using var host = NewHost(settings);

        await StartBoundedAsync(host);

        Assert.False(host.IsRunning);
        Assert.False(string.IsNullOrWhiteSpace(host.LastError));
    }

    [Fact]
    public async Task Disabled_DoesNotListen()
    {
        var settings = NewSettings();
        settings.MediaLinkIsEnabled = false;
        using var host = NewHost(settings);

        await StartBoundedAsync(host);

        Assert.False(host.IsRunning);
        Assert.Null(host.Endpoint);
    }

    [Fact]
    public async Task TimelineIntervalChange_DoesNotRebuildListener()
    {
        // 节流值是热读取的：在设置页拖动它不得断开任何连接（规格 §14.7）。
        var settings = NewSettings();
        using var host = NewHost(settings);
        await StartBoundedAsync(host);
        var endpointBefore = host.Endpoint;

        using var client = await ConnectAsync(endpointBefore!);

        settings.MediaLinkTimelineMinIntervalMs = 500;
        await Task.Delay(DebounceWait);

        Assert.Equal(endpointBefore, host.Endpoint);
        Assert.Equal(WebSocketState.Open, client.State);
    }

    [Fact]
    public async Task AllowedOriginsChange_DoesNotRebuildListener()
    {
        var settings = NewSettings();
        using var host = NewHost(settings);
        await StartBoundedAsync(host);
        var endpointBefore = host.Endpoint;

        using var client = await ConnectAsync(endpointBefore!);

        settings.MediaLinkAllowedOrigins = "null";
        await Task.Delay(DebounceWait);

        Assert.Equal(endpointBefore, host.Endpoint);
        Assert.Equal(WebSocketState.Open, client.State);
    }

    [Fact]
    public async Task TokenChange_RebuildsListener_AndDropsSessions()
    {
        // Token 轮换必须踢掉所有会话，否则旧 Token 的连接能继续用下去。
        var settings = NewSettings();
        using var host = NewHost(settings);
        await StartBoundedAsync(host);

        using var client = await ConnectAsync(host.Endpoint!);
        Assert.Equal(WebSocketState.Open, client.State);

        settings.MediaLinkToken = MediaLinkAuth.GenerateToken();
        await Task.Delay(RebuildWithSessionsWait);

        Assert.True(host.IsRunning, $"LastError={host.LastError}; Endpoint={host.Endpoint}");
        await AssertClientDroppedAsync(client);
    }

    [Fact]
    public async Task RapidPortEdits_RebuildOnceWithFinalValue()
    {
        // 在端口框里逐字输入会产生 1 / 17 / 176 ... 这样的中间态。
        // 断言重建次数而非仅看最终端口：后者在无防抖时也成立，无法证伪。
        var settings = NewSettings();
        using var host = NewHost(settings);
        await StartBoundedAsync(host);

        var rebuilds = 0;
        host.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(host.Endpoint))
            {
                Interlocked.Increment(ref rebuilds);
            }
        };

        var finalPort = FreePort();
        foreach (var port in new[] { 1, 17, 176, 1765, finalPort })
        {
            settings.MediaLinkPort = port;
            await Task.Delay(50); // 快于 500ms 防抖窗口
        }

        await Task.Delay(DebounceWait);

        Assert.True(host.IsRunning, $"LastError={host.LastError}");
        Assert.EndsWith($":{finalPort}/v1/ws", host.Endpoint);
        // 5 次输入合并为 1 次重建。中间态 port=1 需提权，若被真正应用会留下 LastError。
        Assert.Equal(1, Volatile.Read(ref rebuilds));
        Assert.Null(host.LastError);
    }

    private static async Task AssertClientDroppedAsync(ClientWebSocket client)
    {
        var buffer = new byte[1024];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            while (client.State == WebSocketState.Open)
            {
                var result = await client.ReceiveAsync(buffer, cts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
            }
        }
        catch (WebSocketException)
        {
            // 对端直接断开也算被踢掉
        }

        Assert.NotEqual(WebSocketState.Open, client.State);
    }

    private static async Task<ClientWebSocket> ConnectAsync(string endpoint)
    {
        var client = new ClientWebSocket();
        await ConnectBoundedAsync(client, new Uri(endpoint));
        // 收下 server.hello，确保会话已完全建立
        var buffer = new byte[4096];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ReceiveAsync(buffer, cts.Token);
        return client;
    }

    private static PluginSettings NewSettings() => new()
    {
        MediaLinkIsEnabled = true,
        MediaLinkListenAddress = "127.0.0.1",
        MediaLinkPort = FreePort(),
        MediaLinkToken = MediaLinkAuth.GenerateToken(),
        MediaLinkMediaSourceMode = MediaLinkMediaSourceMode.PlatformOnly
    };

    /// <summary>
    /// 带界的宿主启动与连接。规则：凡传给真实 socket 的 I/O 或真实 HostedService 启停的
    /// token，不得为 CancellationToken.None——那样的失效形态是测试进程无输出挂起。
    /// 本类里的 host 是真的 MediaLinkHostedService，会真的监听端口。
    /// </summary>
    private static async Task StartBoundedAsync(MediaLinkHostedService host, int timeoutMs = 10_000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        await host.StartAsync(cts.Token);
    }

    private static async Task ConnectBoundedAsync(ClientWebSocket socket, Uri uri, int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        await socket.ConnectAsync(uri, cts.Token);
    }

    private static MediaLinkHostedService NewHost(PluginSettings settings)
    {
        var media = new FakeMediaService();
        var lyrics = new LyricsSearchService([], [], () => new LyricsSourceSettings());
        var store = new MediaLinkInjectionStore();
        var coordinator = new MediaSourceCoordinator(media, lyrics, store, () => settings);
        var resolver = new MediaPlatformProviderResolver([], NullLogger<MediaPlatformProviderResolver>.Instance);
        return new MediaLinkHostedService(media, lyrics, store, coordinator, resolver, () => settings);
    }

    /// <summary>向 OS 借一个空闲端口后立刻归还，用于避免用例间端口冲突。</summary>
    private static int FreePort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
