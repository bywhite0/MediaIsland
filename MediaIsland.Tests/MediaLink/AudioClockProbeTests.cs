using MediaIsland.Services.MediaLink;
using MediaIsland.Services.MediaLink.Protocol;
using Xunit;

namespace MediaIsland.Tests.MediaLink;

public class AudioClockProbeTests
{
    private const long Ms = TimeSpan.TicksPerMillisecond;

    /// <summary>
    /// 一个由测试推进的时钟。真实时间不参与，故所有时序判据都是确定的。
    /// </summary>
    private sealed class FakeClock
    {
        internal long Now { get; set; }

        internal long Read() => Now;

        internal void Advance(double ms) => Now += (long)(ms * Ms);
    }

    /// <summary>
    /// 一个可编排的对端：每次探测按脚本决定「怎么回」。
    /// </summary>
    private sealed class FakePeer
    {
        private readonly FakeClock _clock;
        private readonly Queue<Func<long, MediaLinkAudioClockPayload?>> _script = new();

        internal FakePeer(FakeClock clock) => _clock = clock;

        internal int Calls { get; private set; }

        /// <summary>
        /// 对端墙钟减对端 QPC，毫秒。信封 ts 由它从 t3 推出——与生产侧一样，
        /// 两个读数取自同一瞬。改它即模拟对端的 NTP 调整。
        /// </summary>
        internal double WallMinusQpcMs { get; set; }

        /// <summary>正常回应：单程各 outboundMs / inboundMs，服务端时钟快 offsetMs。</summary>
        internal FakePeer Answers(double offsetMs, double outboundMs = 1, double inboundMs = 1)
        {
            _script.Enqueue(t1 =>
            {
                _clock.Advance(outboundMs);
                var t2 = _clock.Now + (long)(offsetMs * Ms);
                var payload = new MediaLinkAudioClockPayload
                {
                    For = MediaLinkProtocol.TypeAudioClock, T1 = t1, T2 = t2, T3 = t2
                };
                _clock.Advance(inboundMs);
                return payload;
            });
            return this;
        }

        /// <summary>不回应。</summary>
        internal FakePeer Silent()
        {
            _script.Enqueue(_ => null);
            return this;
        }

        /// <summary>只回三个时刻——对端实现的是 ping 那一档。</summary>
        internal FakePeer AnswersWithoutT2()
        {
            _script.Enqueue(t1 => new MediaLinkAudioClockPayload
            {
                For = MediaLinkProtocol.TypeAudioClock, T1 = t1, T2 = null, T3 = _clock.Now
            });
            return this;
        }

        /// <summary>回显的 t1 是别的请求的——迟到一拍的应答。</summary>
        internal FakePeer AnswersWithStaleEcho()
        {
            _script.Enqueue(t1 => new MediaLinkAudioClockPayload
            {
                For = MediaLinkProtocol.TypeAudioClock, T1 = t1 - 12345, T2 = _clock.Now, T3 = _clock.Now
            });
            return this;
        }

        /// <summary>发送本身就失败。</summary>
        internal FakePeer Throws()
        {
            _script.Enqueue(_ => throw new InvalidOperationException("send failed"));
            return this;
        }

        internal Task<AudioClockProbeReply?> ExchangeAsync(long t1, CancellationToken _)
        {
            Calls++;
            var step = _script.Count > 0 ? _script.Dequeue() : (_ => null);
            var payload = step(t1);
            return Task.FromResult(payload is null
                ? default(AudioClockProbeReply?)
                : new AudioClockProbeReply(
                    payload,
                    ((payload.T3 ?? 0) + (long)(WallMinusQpcMs * Ms)) / 10_000));
        }
    }

    private static (AudioClockProbe Probe, FakeClock Clock, FakePeer Peer) Build(Action<FakePeer> script)
    {
        var clock = new FakeClock();
        var peer = new FakePeer(clock);
        script(peer);
        return (new AudioClockProbe(peer.ExchangeAsync, clock.Read), clock, peer);
    }

    [Fact]
    public async Task SuccessfulProbe_MakesTheOffsetAvailable()
    {
        var (probe, _, _) = Build(p => p.Answers(offsetMs: 40));

        Assert.True(await probe.ProbeOnceAsync(CancellationToken.None));
        Assert.True(probe.TryGetOffset(out var offset, out var rtt));
        Assert.Equal(40 * Ms, offset);
        Assert.Equal(2 * Ms, rtt);
    }

    [Fact]
    public async Task FastPhase_LastsUntilTheWindowIsFull()
    {
        var (probe, _, _) = Build(p =>
        {
            for (var i = 0; i < AudioClockProbe.FastProbeCount; i++)
            {
                p.Answers(offsetMs: 40);
            }
        });

        for (var i = 0; i < AudioClockProbe.FastProbeCount; i++)
        {
            Assert.Equal(AudioClockProbe.FastInterval, probe.NextInterval);
            Assert.True(await probe.ProbeOnceAsync(CancellationToken.None));
        }

        Assert.Equal(AudioClockProbe.SteadyInterval, probe.NextInterval);
    }

    [Fact]
    public async Task FailedProbes_DoNotAdvanceTheFastPhase()
    {
        // 阶段按「已接受的样本数」判，不按「已发出的探测数」。没配成样本时窗口
        // 仍是空的，此时降速只会让收敛更慢。
        var (probe, _, _) = Build(p =>
        {
            for (var i = 0; i < AudioClockProbe.FastProbeCount * 2; i++)
            {
                p.Silent();
            }
        });

        for (var i = 0; i < AudioClockProbe.FastProbeCount * 2; i++)
        {
            await probe.ProbeOnceAsync(CancellationToken.None);
        }

        // 先钉住「探测确实跑了」再断言阶段。FastInterval 同时也是初始值，
        // 少了这一条地基，一个压根不执行探测的实现同样能让下面那条断言为真。
        Assert.Equal(AudioClockProbe.FastProbeCount * 2, probe.Misses);
        Assert.Equal(0, probe.AcceptedSamples);
        Assert.Equal(AudioClockProbe.FastInterval, probe.NextInterval);
    }

    [Fact]
    public async Task MissingT2_IsTreatedAsUnsupportedRatherThanDegraded()
    {
        var (probe, _, peer) = Build(p => p.AnswersWithoutT2());

        Assert.False(await probe.ProbeOnceAsync(CancellationToken.None));

        // 地基：对端确实被调过一次。少了它，一个压根不发探测的实现也能让
        // 下面三条断言全为真（IsUnsupported 若被误置、TryGetOffset 空窗本就为假）。
        Assert.Equal(1, peer.Calls);
        Assert.True(probe.IsUnsupported);
        Assert.False(probe.TryGetOffset(out _, out _));

        // 确认不支持之后不再打扰对端。
        Assert.False(await probe.ProbeOnceAsync(CancellationToken.None));
        Assert.Equal(1, peer.Calls);
    }

    [Fact]
    public async Task MissingT3_IsAlsoTreatedAsUnsupported()
    {
        // 「四时间戳缺一不可」有两个分支，此前只测了缺 t2 那个。缺 t3 的判定走的是
        // 同一个条件式的后半段，把它删掉时缺 t2 那条判据仍然全绿。
        var clock = new FakeClock();
        var probe = new AudioClockProbe(
            (t1, _) => Task.FromResult<AudioClockProbeReply?>(new AudioClockProbeReply(
                new MediaLinkAudioClockPayload { T1 = t1, T2 = 10 * Ms, T3 = null }, 0)),
            clock.Read);

        Assert.False(await probe.ProbeOnceAsync(CancellationToken.None));
        Assert.True(probe.IsUnsupported);
        Assert.False(probe.TryGetOffset(out _, out _));
    }

    [Fact]
    public async Task ThreeConsecutiveMisses_DropTheWholeWindow()
    {
        var (probe, _, _) = Build(p => p
            .Answers(offsetMs: 40)
            .Silent().Silent().Silent());

        Assert.True(await probe.ProbeOnceAsync(CancellationToken.None));
        Assert.True(probe.TryGetOffset(out _, out _));

        for (var i = 0; i < AudioClockProbe.MaxConsecutiveMisses; i++)
        {
            Assert.False(await probe.ProbeOnceAsync(CancellationToken.None));
        }

        Assert.False(probe.TryGetOffset(out _, out _));
        Assert.Equal(AudioClockProbe.MaxConsecutiveMisses, probe.Misses);
    }

    [Fact]
    public async Task TwoMissesThenSuccess_KeepsTheOffsetAvailable()
    {
        // 单次丢包在无线链路上是常态。三次才清窗的整个意义就在这条判据里：
        // 少于阈值的中断不该让 offset 反复进出可用状态。
        var (probe, _, _) = Build(p => p
            .Answers(offsetMs: 40)
            .Silent().Silent()
            .Answers(offsetMs: 40)
            .Silent().Silent());

        Assert.True(await probe.ProbeOnceAsync(CancellationToken.None));
        await probe.ProbeOnceAsync(CancellationToken.None);
        await probe.ProbeOnceAsync(CancellationToken.None);
        Assert.True(await probe.ProbeOnceAsync(CancellationToken.None));
        await probe.ProbeOnceAsync(CancellationToken.None);
        await probe.ProbeOnceAsync(CancellationToken.None);

        Assert.True(probe.TryGetOffset(out var offset, out _));
        Assert.Equal(40 * Ms, offset);
    }

    [Fact]
    public async Task StaleEcho_IsDiscardedInsteadOfPaired()
    {
        var (probe, _, _) = Build(p => p.AnswersWithStaleEcho());

        Assert.False(await probe.ProbeOnceAsync(CancellationToken.None));
        Assert.Equal(1, probe.Mismatches);
        Assert.False(probe.TryGetOffset(out _, out _));
    }

    [Fact]
    public async Task ExchangeFailure_IsCountedAsAMissAndNotPropagated()
    {
        var (probe, _, _) = Build(p => p.Throws());

        Assert.False(await probe.ProbeOnceAsync(CancellationToken.None));
        Assert.Equal(1, probe.Misses);
    }

    [Fact]
    public async Task NegativeRoundTrip_IsCountedAsRejectedNotAccepted()
    {
        // 负往返的真实形态：对端自称的处理耗时（t3 - t2）超过客户端观测到的整个
        // 往返（t4 - t1）。这是字段错位或对端时钟中途被重置的证据。
        //
        // 注意它不是「t3 早于 t2」——那反而让往返变大而非变负，写这条判据时先算错过一次。
        var clock = new FakeClock();
        var probe = new AudioClockProbe(
            (t1, _) => Task.FromResult<AudioClockProbeReply?>(new AudioClockProbeReply(
                new MediaLinkAudioClockPayload { T1 = t1, T2 = 100 * Ms, T3 = 900 * Ms }, 0)),
            clock.Read);

        Assert.False(await probe.ProbeOnceAsync(CancellationToken.None));
        Assert.Equal(1, probe.RejectedSamples);

        // 被拒也计一次「没成」。两个计数并存而不是二选一：RejectedSamples 说的是
        // 「坏在哪」，Misses 说的是「连续多少次没成」，后者才是清窗的依据。
        // 本条断言此前写的是 Misses 恒为 0，那等于把「恒产出坏样本的对端永远不清窗」
        // 这个缺陷钉成了预期行为。
        Assert.Equal(1, probe.Misses);
        Assert.False(probe.TryGetOffset(out _, out _));
    }

    [Fact]
    public async Task Reset_ClearsTheUnsupportedVerdictSoAnotherPeerGetsAChance()
    {
        // 实例跨重连复用，而重连后的对端可以是另一台机器。不清 IsUnsupported 就意味着
        // 一旦连过一个只实现三时间戳的对端，此后连到任何机器都不再对时，
        // 且诊断说「对端不支持」——指向的是错误的那一台。
        var (probe, _, peer) = Build(p => p.AnswersWithoutT2().Answers(offsetMs: 40));

        Assert.False(await probe.ProbeOnceAsync(CancellationToken.None));
        Assert.True(probe.IsUnsupported);

        probe.Reset();

        Assert.False(probe.IsUnsupported);
        Assert.True(await probe.ProbeOnceAsync(CancellationToken.None));
        Assert.True(probe.TryGetOffset(out var offset, out _));
        Assert.Equal(40 * Ms, offset);
        Assert.Equal(2, peer.Calls);
    }

    [Fact]
    public async Task PeerThatAlwaysAnswersWithUnusableSamples_EventuallyLosesTheOffset()
    {
        // 每次都回应故永不计「无回应」，但回的样本恒被判负往返——形态是对端把 t2 取自
        // 单调时钟而 t3 取自墙钟。若被拒样本不触发失效，最后一个被接受的 offset 会被
        // 无限期沿用，而没有任何计数说得出它已经很旧了。
        var clock = new FakeClock();
        var good = true;
        var probe = new AudioClockProbe(
            (t1, _) =>
            {
                var payload = good
                    ? new MediaLinkAudioClockPayload { T1 = t1, T2 = 40 * Ms, T3 = 40 * Ms }
                    : new MediaLinkAudioClockPayload { T1 = t1, T2 = 100 * Ms, T3 = 900 * Ms };
                good = false;
                return Task.FromResult<AudioClockProbeReply?>(new AudioClockProbeReply(payload, 0));
            },
            clock.Read);

        Assert.True(await probe.ProbeOnceAsync(CancellationToken.None));
        Assert.True(probe.TryGetOffset(out _, out _));

        for (var i = 0; i < AudioClockProbe.MaxConsecutiveMisses; i++)
        {
            Assert.False(await probe.ProbeOnceAsync(CancellationToken.None));
        }

        Assert.False(probe.TryGetOffset(out _, out _));
        Assert.Equal(AudioClockProbe.MaxConsecutiveMisses, probe.RejectedSamples);
    }

    [Fact]
    public async Task RunAsync_UsesTheFastIntervalUntilTheWindowIsFullThenSlowsDown()
    {
        var (probe, _, _) = Build(p =>
        {
            for (var i = 0; i < AudioClockProbe.FastProbeCount + 2; i++)
            {
                p.Answers(offsetMs: 40);
            }
        });

        var waits = new List<TimeSpan>();
        using var cts = new CancellationTokenSource();
        await probe.RunAsync((interval, _) =>
        {
            waits.Add(interval);
            if (waits.Count >= AudioClockProbe.FastProbeCount + 2)
            {
                cts.Cancel();
            }

            return Task.CompletedTask;
        }, cts.Token);

        Assert.Equal(AudioClockProbe.FastProbeCount + 2, waits.Count);
        Assert.All(waits.Take(AudioClockProbe.FastProbeCount - 1),
            w => Assert.Equal(AudioClockProbe.FastInterval, w));
        Assert.Equal(AudioClockProbe.SteadyInterval, waits[^1]);
    }

    [Fact]
    public async Task RunAsync_StopsAsSoonAsThePeerIsKnownUnsupported()
    {
        var (probe, _, peer) = Build(p => p.AnswersWithoutT2());

        // 循环次数必须有界，不能只靠 IsUnsupported 终止。第一版就是那么写的，
        // 而「缺 t2 时退化估计而非报不支持」这个变异让它挂住而不是变红——
        // 判据挂住等于没有判据，且症状与本期要消灭的无界等待同形。
        // 这里由 delay 的调用次数封顶：变异后 waits 会涨到上限并取消，
        // 于是 IsUnsupported 那条断言变红。
        var waits = 0;
        using var cts = new CancellationTokenSource();
        await probe.RunAsync((_, _) =>
        {
            if (++waits >= 5)
            {
                cts.Cancel();
            }

            return Task.CompletedTask;
        }, cts.Token);

        Assert.True(probe.IsUnsupported);
        Assert.Equal(0, waits);
        Assert.Equal(1, peer.Calls);
    }

    [Fact]
    public async Task RunAsync_SurvivesAnEndlesslyFailingPeer()
    {
        // 对时不可用不该拖垮任何东西。这条判据钉的是「不抛」，
        // 因为 media / lyrics / 转发与它共用同一条连接。
        var (probe, _, _) = Build(p =>
        {
            for (var i = 0; i < 20; i++)
            {
                p.Throws();
            }
        });

        var waits = 0;
        using var cts = new CancellationTokenSource();
        await probe.RunAsync((_, _) =>
        {
            if (++waits >= 20)
            {
                cts.Cancel();
            }

            return Task.CompletedTask;
        }, cts.Token);

        Assert.Equal(20, waits);
        // 同样是地基：20 次等待可能来自 20 次什么都没做的空转。
        Assert.Equal(20, probe.Misses);
        Assert.False(probe.IsUnsupported);
        Assert.False(probe.TryGetOffset(out _, out _));
    }

    [Fact]
    public async Task WireOffset_MapsTheSenderWallClockAxisOntoTheLocalQpcAxis()
    {
        // 目标出声时刻的算式是「帧头时刻（发送端墙钟）+ D + offset」，故 offset 必须是
        // 本机 QPC 减发送端墙钟。逐 tick 钉死合成：t1 = 0、t4 = 2ms，QPC 轴 offset =
        // ((10 − 0) + (11 − 2)) / 2 = 9.5ms；信封 ts = 36ms 与 t3 = 11ms 同瞬取得，
        // 桥 = 36 − 11 = 25ms；线上 offset = −9.5 − 25 = −34.5ms。
        // t2 与 t3 刻意不相等：桥若配了 t2（收到时而非发出时），这里会差出 1ms。
        var clock = new FakeClock();
        var probe = new AudioClockProbe(
            (t1, _) =>
            {
                clock.Advance(2);
                return Task.FromResult<AudioClockProbeReply?>(new AudioClockProbeReply(
                    new MediaLinkAudioClockPayload { T1 = t1, T2 = 10 * Ms, T3 = 11 * Ms },
                    EnvelopeTsMs: 36));
            },
            clock.Read);

        Assert.True(await probe.ProbeOnceAsync(CancellationToken.None));
        Assert.True(probe.TryGetWireOffset(out var wire, out var rtt));
        Assert.Equal(-345_000, wire);
        Assert.Equal(1 * Ms, rtt);
    }

    [Fact]
    public async Task WireOffset_IsUnavailableUntilASampleIsAccepted()
    {
        var (probe, _, _) = Build(p => p.Silent());

        Assert.False(probe.TryGetWireOffset(out _, out _));
        await probe.ProbeOnceAsync(CancellationToken.None);
        Assert.False(probe.TryGetWireOffset(out _, out _));
    }

    [Fact]
    public async Task WireOffset_TakesTheWallBridgeFromTheLatestAcceptedSample()
    {
        // QPC 轴 offset 走最小往返选择（受排队延迟污染最少的那次），而墙钟桥取最近
        // 一次被接受的样本：W 是准常量，最新读数才反映对端此刻的墙钟——对端 NTP step
        // 之后仍沿用旧桥，会把一个已经不存在的墙钟状态钉进每一帧的目标时刻。
        var clock = new FakeClock();
        var peer = new FakePeer(clock) { WallMinusQpcMs = 25 };
        peer.Answers(offsetMs: 40);
        var probe = new AudioClockProbe(peer.ExchangeAsync, clock.Read);

        Assert.True(await probe.ProbeOnceAsync(CancellationToken.None));

        // 第二个样本往返更大（不会被选中），但它的桥要生效。
        peer.WallMinusQpcMs = 30;
        peer.Answers(offsetMs: 40, outboundMs: 5, inboundMs: 5);
        Assert.True(await probe.ProbeOnceAsync(CancellationToken.None));

        Assert.True(probe.TryGetWireOffset(out var wire, out var rtt));
        Assert.Equal(2 * Ms, rtt);
        Assert.Equal(-(40 + 30) * Ms, wire);
    }
}
