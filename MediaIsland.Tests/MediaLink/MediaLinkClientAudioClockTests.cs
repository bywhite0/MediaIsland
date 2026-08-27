using MediaIsland.Services.MediaLink.Protocol;
using Xunit;

namespace MediaIsland.Tests.MediaLink;

/// <summary>
/// 客户端的对时探测调度。探测壳与估计器的行为在 AudioClockProbeTests 已经钉住，
/// 这里钉的是「谁来跑它」：连上就自己探、应答路由回等待者、能力中途出现也能接上。
/// 这一层断的话没有任何报错——offset 永远不可用，对齐永远报「还没对上时钟」，
/// 而那条提示指向等待。
/// </summary>
public class MediaLinkClientAudioClockTests
{
    private const string ClockCapability = ""","capabilities":["audio.clock"]""";

    private static bool IsClockRequest(string sent) =>
        sent.Contains("\"type\":\"audio.clock\"", StringComparison.Ordinal);

    private static long RequestT1(string sent)
    {
        var message = MediaLinkMessageSerializer.Deserialize(sent);
        var payload = MediaLinkMessageSerializer
            .DeserializePayload<MediaLinkAudioClockRequestPayload>(message!.Payload);
        return payload!.T1!.Value;
    }

    /// <summary>
    /// 一条与请求回显匹配的应答。t2/t3 取在 t1 之后几个 tick——数值本身不重要
    /// （对端时钟轴是任意的），重要的是回显对得上且往返非负。
    /// </summary>
    private static string MatchingAnswer(long t1) =>
        """{"type":"audio.clock","v":1,"ts":1700000000000,"id":"clk","payload":{"for":"audio.clock","t1":@T1,"t2":@T2,"t3":@T3}}"""
            .Replace("@T1", t1.ToString())
            .Replace("@T2", (t1 + 5).ToString())
            .Replace("@T3", (t1 + 6).ToString());

    [Fact]
    public async Task ConnectedClient_WithTheCapability_ProbesOnItsOwn()
    {
        await using var harness = await MediaLinkClientCapabilityHarness.ConnectAsync(ClockCapability);

        await harness.WaitUntilAsync(() => harness.Socket.Sent.Any(IsClockRequest));

        var request = harness.Socket.Sent.FirstOrDefault(IsClockRequest);
        Assert.NotNull(request);
        // 请求必须带 t1：没有回显，迟到与重复的应答就能配成样本。
        Assert.True(RequestT1(request) != 0);
    }

    [Fact]
    public async Task AMatchingAnswer_MakesTheWireOffsetAvailable()
    {
        await using var harness = await MediaLinkClientCapabilityHarness.ConnectAsync(ClockCapability);

        // 应答方脚本：每见到一条新请求就回一条回显匹配的应答。逐条都答而不是只答第一条，
        // 慢机器上第一次往返可能超时，判据不该被那种时序绑死。
        var answered = new HashSet<long>();
        await harness.WaitUntilAsync(() =>
        {
            foreach (var sent in harness.Socket.Sent.Where(IsClockRequest))
            {
                var t1 = RequestT1(sent);
                if (answered.Add(t1))
                {
                    harness.Socket.QueueRaw(MatchingAnswer(t1));
                }
            }

            return harness.Client.AudioClockOffsetAvailable;
        });

        Assert.True(harness.Client.AudioClockOffsetAvailable);
        Assert.True(harness.Client.TryGetAudioClockWireOffset(out _, out var roundTrip));
        Assert.True(roundTrip >= 0);
    }

    [Fact]
    public async Task LegacyServer_IsNeverProbed()
    {
        // 未声明能力就不发 audio.clock——向老服务端发未知类型换来的是 bad_request 或
        // 静默丢弃，两者都只是浪费，且日志里会多出一类查不出所以然的错误。
        await using var harness = await MediaLinkClientCapabilityHarness.ConnectAsync(capabilitiesJson: "");

        // 负向条件给足窗口：快速阶段的探测周期是 200ms，能力在的话 500ms 内必有请求。
        await Task.Delay(500);

        Assert.DoesNotContain(harness.Socket.Sent, IsClockRequest);
    }

    [Fact]
    public async Task CapabilityArrivingMidSession_StartsProbing()
    {
        // server.hello 可在会话中途重发并新增能力（服务端升级、采集设备回来了）。
        // 探测调度若只在握手时看一眼，中途出现的能力就永远等不来对时。
        await using var harness = await MediaLinkClientCapabilityHarness.ConnectAsync(capabilitiesJson: "");

        harness.QueueServerHello(epoch: 2, ClockCapability);

        await harness.WaitUntilAsync(() => harness.Socket.Sent.Any(IsClockRequest));
        Assert.Contains(harness.Socket.Sent, IsClockRequest);
    }
}
