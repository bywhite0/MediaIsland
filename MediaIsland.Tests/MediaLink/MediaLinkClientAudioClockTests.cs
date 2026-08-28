using MediaIsland.Services.MediaLink;
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
        //
        // 负向条件不靠墙钟：注入等待器数「循环歇了几轮」。歇一轮就证明循环在这一轮
        // 选了「不探」分支；数满三轮即三轮都没探。脚本前两轮立即放行，第三轮起长驻
        // 到断连取消——放行是为了让轮次真的发生，长驻是为了不让循环空转到测试结束。
        var rounds = 0;
        var intervals = new List<TimeSpan>();
        await using var harness = await MediaLinkClientCapabilityHarness.ConnectAsync(
            capabilitiesJson: "",
            probeWait: (interval, ct) =>
            {
                lock (intervals)
                {
                    intervals.Add(interval);
                }

                return Interlocked.Increment(ref rounds) < 3
                    ? Task.CompletedTask
                    : Task.Delay(Timeout.Infinite, ct);
            });

        await harness.WaitUntilAsync(() => Volatile.Read(ref rounds) >= 3);
        Assert.True(Volatile.Read(ref rounds) >= 3);

        Assert.DoesNotContain(harness.Socket.Sent, IsClockRequest);

        // 等待器脚本判据：没能力时每轮歇的都是稳态间隔。循环把间隔改小（极端是 0）
        // 意味着对老服务端空转刷 CPU，这条就是防那种回归的。
        lock (intervals)
        {
            Assert.All(intervals, interval => Assert.Equal(AudioClockProbe.SteadyInterval, interval));
        }
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

    [Fact]
    public async Task AShrunkenBudget_DoesNotDisturbMediaLyricsOrAudioFrames()
    {
        // D 改小会让对齐退回，但退回改的只是播放的对齐参数——media、lyrics 与
        // 二进制音频帧三条通道和它共用同一条连接，谁都不该被波及。
        // 波及的形态不是报错而是「换了服务端配置之后歌词不动了」，从症状查不回原因。
        await using var harness = await MediaLinkClientCapabilityHarness.ConnectAsync(
            ""","capabilities":["audio","audio.clock"],"audioClock":{"dMs":300}""");

        // 对齐重算订阅在这个事件上，而它可能抛（读设备事实撞上任何意外）。
        // 抛出不得把一次归因失败升级成整条连接重连——那正是本判据要防的波及路径。
        harness.Client.AudioClockStateChanged += (_, _) => throw new InvalidOperationException("hostile");

        harness.QueueServerHello(
            epoch: 2, ""","capabilities":["audio","audio.clock"],"audioClock":{"dMs":55}""");
        await harness.WaitUntilAsync(() => harness.Client.ServerAudioClockBudgetMs == 55);
        Assert.Equal(55, harness.Client.ServerAudioClockBudgetMs);

        var media = 0;
        var lyrics = 0;
        var frames = 0;
        harness.Client.MediaReceived += (_, _) => Interlocked.Increment(ref media);
        harness.Client.LyricsReceived += (_, _) => Interlocked.Increment(ref lyrics);
        harness.Client.AudioFrameReceived += (_, _) => Interlocked.Increment(ref frames);

        harness.Socket.QueueMediaUpdated(seq: 1, title: "Song");
        harness.Socket.QueueLyricsUpdated(seq: 2, trackToken: "tok");
        harness.Socket.QueueBinary([0xA1, 0x01, 0x01]);

        await harness.WaitUntilAsync(() =>
            Volatile.Read(ref media) == 1 && Volatile.Read(ref lyrics) == 1 && Volatile.Read(ref frames) == 1);
        Assert.Equal(1, Volatile.Read(ref media));
        Assert.Equal(1, Volatile.Read(ref lyrics));
        Assert.Equal(1, Volatile.Read(ref frames));
    }
}
