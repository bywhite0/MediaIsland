using System.Text.Json;
using MediaIsland.Services.MediaLink;
using MediaIsland.Services.MediaLink.Protocol;
using Xunit;

namespace MediaIsland.Tests.MediaLink;

/// <summary>
/// server.hello 的能力协商。老服务端不认识 audio 频道会返回 bad_request，
/// 客户端必须能在订阅前判断对端是否支持，而不是靠试错。
/// </summary>
public class MediaLinkCapabilitiesTests
{
    [Fact]
    public void ServerHello_SerializesCapabilitiesAndAudioFormat()
    {
        var payload = new MediaLinkServerHelloPayload
        {
            SessionEpoch = 3,
            Capabilities = [MediaLinkProtocol.CapabilityAudio],
            Audio = new MediaLinkAudioFormatPayload()
        };

        var json = JsonSerializer.Serialize(payload);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("protocolVersion").GetInt32());
        Assert.True(root.GetProperty("authRequired").GetBoolean());
        Assert.Equal(3, root.GetProperty("sessionEpoch").GetInt64());
        Assert.Equal("audio", root.GetProperty("capabilities")[0].GetString());
        Assert.Equal(48000, root.GetProperty("audio").GetProperty("sampleRate").GetInt32());
        Assert.Equal(2, root.GetProperty("audio").GetProperty("channels").GetInt32());
        Assert.Equal("s16le", root.GetProperty("audio").GetProperty("format").GetString());
    }

    [Fact]
    public void ServerHello_OmitsCapabilities_WhenNull()
    {
        // 未声明能力时不应产出空字段——老客户端解析到 null 数组可能崩。
        var json = JsonSerializer.Serialize(new MediaLinkServerHelloPayload { SessionEpoch = 1 });

        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.TryGetProperty("capabilities", out _));
        Assert.False(document.RootElement.TryGetProperty("audio", out _));
    }

    [Fact]
    public void ServerHello_DeserializesFromLegacyPayload()
    {
        // 老服务端的 hello 没有这两个字段，反序列化后必须是 null 而非抛异常。
        const string legacy = """{"protocolVersion":1,"authRequired":true,"sessionEpoch":5}""";

        var payload = JsonSerializer.Deserialize<MediaLinkServerHelloPayload>(legacy);

        Assert.NotNull(payload);
        Assert.Equal(5, payload.SessionEpoch);
        Assert.Null(payload.Capabilities);
        Assert.Null(payload.Audio);
    }

    [Fact]
    public void ServerHello_RoundTripsCapabilitiesAndAudioFormat()
    {
        // 新客户端读新服务端：能力字段必须能原样读回，否则协商无从谈起。
        var json = JsonSerializer.Serialize(new MediaLinkServerHelloPayload
        {
            SessionEpoch = 7,
            Capabilities = [MediaLinkProtocol.CapabilityAudio],
            Audio = new MediaLinkAudioFormatPayload()
        });

        var payload = JsonSerializer.Deserialize<MediaLinkServerHelloPayload>(json);

        Assert.NotNull(payload);
        Assert.Equal([MediaLinkProtocol.CapabilityAudio], payload.Capabilities);
        Assert.NotNull(payload.Audio);
        Assert.Equal(MediaLinkProtocol.AudioSampleRate, payload.Audio.SampleRate);
        Assert.Equal(MediaLinkProtocol.AudioChannels, payload.Audio.Channels);
        Assert.Equal(MediaLinkProtocol.AudioFormat, payload.Audio.Format);
    }

    [Fact]
    public void AudioChannel_IsKnownChannel()
    {
        Assert.Contains(MediaLinkProtocol.ChannelAudio, MediaLinkProtocol.KnownChannels);
    }

    [Fact]
    public void KnownChannels_StillContainsExistingChannels()
    {
        // 回归锁定：新增频道不得影响既有两条。
        Assert.Contains(MediaLinkProtocol.ChannelMedia, MediaLinkProtocol.KnownChannels);
        Assert.Contains(MediaLinkProtocol.ChannelLyrics, MediaLinkProtocol.KnownChannels);
        Assert.Equal(3, MediaLinkProtocol.KnownChannels.Count);
    }

    [Fact]
    public void AudioControlTypes_UseNamespacedWireValues()
    {
        // 线上取值即客户端要发的字符串，改动等同破坏协议，故锁死字面量。
        Assert.Equal("audio.play_start", MediaLinkProtocol.TypeAudioPlayStart);
        Assert.Equal("audio.play_stop", MediaLinkProtocol.TypeAudioPlayStop);
    }
}

/// <summary>
/// 客户端对服务端能力的感知。订阅是一次性的整体请求：向老服务端请求
/// ["media","lyrics","audio"] 会被整体拒绝，media 和 lyrics 也一起失效。
/// 因此第 3 期订阅音频前，必须先有这里存下来的能力信息。
/// </summary>
public class MediaLinkClientCapabilityTests
{
    [Fact]
    public void ParseHello_WithAudioCapability_YieldsCapabilityList()
    {
        const string hello = """
            {"protocolVersion":1,"authRequired":true,"sessionEpoch":1,
             "capabilities":["audio"],
             "audio":{"sampleRate":48000,"channels":2,"format":"s16le"}}
            """;

        var payload = JsonSerializer.Deserialize<MediaLinkServerHelloPayload>(hello);

        Assert.NotNull(payload!.Capabilities);
        Assert.Contains(MediaLinkProtocol.CapabilityAudio, payload.Capabilities);
    }

    [Fact]
    public async Task Client_CapableServer_ReportsSupportsAudio()
    {
        await using var harness = await MediaLinkClientCapabilityHarness.ConnectAsync(
            capabilitiesJson: ""","capabilities":["audio"]""");

        Assert.True(harness.Client.SupportsAudio);
        Assert.Contains(MediaLinkProtocol.CapabilityAudio, harness.Client.ServerCapabilities);
    }

    [Fact]
    public async Task Client_LegacyServer_ReportsNoAudioSupport()
    {
        // 老服务端的 hello 没有 capabilities 字段：必须是空集合而非 null，
        // 且不得让连接流程抛异常。
        await using var harness = await MediaLinkClientCapabilityHarness.ConnectAsync(
            capabilitiesJson: "");

        Assert.False(harness.Client.SupportsAudio);
        Assert.Empty(harness.Client.ServerCapabilities);
    }

    [Fact]
    public async Task Client_UnknownCapability_IgnoredWithoutError()
    {
        // 未来服务端可能声明本客户端不认识的能力，收下但不误判为支持音频。
        await using var harness = await MediaLinkClientCapabilityHarness.ConnectAsync(
            capabilitiesJson: ""","capabilities":["quantum-teleport"]""");

        Assert.False(harness.Client.SupportsAudio);
        Assert.Contains("quantum-teleport", harness.Client.ServerCapabilities);
    }

    [Fact]
    public async Task Client_DeclaredBudget_IsParsed()
    {
        await using var harness = await MediaLinkClientCapabilityHarness.ConnectAsync(
            capabilitiesJson: ""","capabilities":["audio","audio.clock"],"audioClock":{"dMs":250}""");

        Assert.True(harness.Client.SupportsAudioClock);
        Assert.Equal(250, harness.Client.ServerAudioClockBudgetMs);
    }

    [Fact]
    public async Task Client_MissingAudioClockObject_LeavesTheBudgetUnset()
    {
        // 缺失不回落到默认值：两端各自默认成同一个数看起来一致，实则两份各自为真的
        // 声明，改了一端就静默失配。声明了能力但没给参数同样算没声明。
        await using var harness = await MediaLinkClientCapabilityHarness.ConnectAsync(
            capabilitiesJson: ""","capabilities":["audio","audio.clock"]""");

        Assert.True(harness.Client.SupportsAudioClock);
        Assert.Null(harness.Client.ServerAudioClockBudgetMs);
    }

    [Fact]
    public async Task Client_ADeclaredZero_IsNotTheSameAsUndeclared()
    {
        // 「声明了一个办不到的值」与「没声明」的排查方向不同：改服务端配置 / 查服务端
        // 版本。拿 -1 或 0 当空值哨兵会把前者吞成后者。
        await using var harness = await MediaLinkClientCapabilityHarness.ConnectAsync(
            capabilitiesJson: ""","capabilities":["audio.clock"],"audioClock":{"dMs":0}""");

        Assert.Equal(0, harness.Client.ServerAudioClockBudgetMs);
    }

    [Fact]
    public async Task Client_HelloDuringSession_RereadsTheBudgetBothWays()
    {
        // server.hello 可在连接存活期间重发并改 D。只在握手记一次的话，改小时接收端
        // 不会退回、改大时不会重新进入，而两种情形都不报错。
        await using var harness = await MediaLinkClientCapabilityHarness.ConnectAsync(
            capabilitiesJson: ""","capabilities":["audio.clock"],"audioClock":{"dMs":300}""");
        Assert.Equal(300, harness.Client.ServerAudioClockBudgetMs);

        harness.QueueServerHello(
            epoch: 2,
            capabilitiesJson: ""","capabilities":["audio.clock"],"audioClock":{"dMs":120}""");
        await harness.WaitUntilAsync(() => harness.Client.ServerAudioClockBudgetMs == 120);
        Assert.Equal(120, harness.Client.ServerAudioClockBudgetMs);

        // 再改回来要能重新进入，且声明整个消失时要落回空值。
        harness.QueueServerHello(epoch: 3, capabilitiesJson: ""","capabilities":["audio.clock"]""");
        await harness.WaitUntilAsync(() => harness.Client.ServerAudioClockBudgetMs is null);
        Assert.Null(harness.Client.ServerAudioClockBudgetMs);
    }

    [Fact]
    public async Task Client_HelloDuringSession_RefreshesCapabilities()
    {
        // 服务端重建会话后会再 hello 一次，此时能力可能已经变了（如采集设备掉了）。
        // 只在握手记一次，客户端会拿过期能力去订阅 audio，而 subscribe 是整体替换，
        // 被拒时 media 与 lyrics 一起失效。
        await using var harness = await MediaLinkClientCapabilityHarness.ConnectAsync(
            capabilitiesJson: ""","capabilities":["audio"]""");
        Assert.True(harness.Client.SupportsAudio);

        harness.QueueServerHello(epoch: 2, capabilitiesJson: "");
        await harness.WaitUntilAsync(() => !harness.Client.SupportsAudio);

        Assert.False(harness.Client.SupportsAudio);
        Assert.Empty(harness.Client.ServerCapabilities);
    }
}

/// <summary>
/// 驱动 MediaLinkClient 走完一次握手的夹具。复用 MediaLinkClientTests 的
/// ScriptedClientSocket——能力感知只关心 hello 里的字段，不需要真实网络时序。
/// </summary>
internal sealed class MediaLinkClientCapabilityHarness(ScriptedClientSocket socket, MediaLinkClient client)
    : IAsyncDisposable
{
    /// <summary>
    /// 手写 hello 而非序列化 payload 对象：只有原始 JSON 能表达「capabilities 字段
    /// 根本不存在」以及本客户端不认识的能力取值这两种情形。
    /// </summary>
    private const string HelloPrefix = """
        {"type":"event","name":"server.hello","v":1,"ts":0,"payload":{"protocolVersion":1,"authRequired":true,"sessionEpoch":
        """;

    public MediaLinkClient Client { get; } = client;

    /// <param name="capabilitiesJson">拼进 hello payload 的原始片段，需自带前导逗号；
    /// 空串即模拟不声明任何能力的老服务端。</param>
    public static async Task<MediaLinkClientCapabilityHarness> ConnectAsync(string capabilitiesJson)
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

        client.Start();
        // auth 与 subscribe 都发出即说明 hello 已被解析——记录能力发生在 auth 之前。
        await socket.WaitForSendsAsync(2);
        return new MediaLinkClientCapabilityHarness(socket, client);
    }

    /// <summary>会话中途再投一条 hello，模拟服务端重建会话后的重新声明。</summary>
    public void QueueServerHello(long epoch, string capabilitiesJson) =>
        socket.QueueRaw(BuildHello(epoch, capabilitiesJson));

    public async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }

    private static string BuildHello(long epoch, string capabilitiesJson) =>
        HelloPrefix + epoch + capabilitiesJson + "}}";

    public async ValueTask DisposeAsync() => await Client.DisposeAsync();
}
