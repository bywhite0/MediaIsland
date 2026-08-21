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

        internal Task<MediaLinkAudioClockPayload?> ExchangeAsync(long t1, CancellationToken _)
        {
            Calls++;
            var step = _script.Count > 0 ? _script.Dequeue() : (_ => null);
            return Task.FromResult(step(t1));
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

        Assert.Equal(AudioClockProbe.FastInterval, probe.NextInterval);
    }

    [Fact]
    public async Task MissingT2_IsTreatedAsUnsupportedRatherThanDegraded()
    {
        var (probe, _, peer) = Build(p => p.AnswersWithoutT2().Answers(offsetMs: 40));

        Assert.False(await probe.ProbeOnceAsync(CancellationToken.None));
        Assert.True(probe.IsUnsupported);
        Assert.False(probe.TryGetOffset(out _, out _));

        // 确认不支持之后不再打扰对端。
        var callsBefore = peer.Calls;
        Assert.False(await probe.ProbeOnceAsync(CancellationToken.None));
        Assert.Equal(callsBefore, peer.Calls);
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
            (t1, _) => Task.FromResult<MediaLinkAudioClockPayload?>(new MediaLinkAudioClockPayload
            {
                T1 = t1, T2 = 100 * Ms, T3 = 900 * Ms
            }),
            clock.Read);

        Assert.False(await probe.ProbeOnceAsync(CancellationToken.None));
        Assert.Equal(1, probe.RejectedSamples);
        Assert.Equal(0, probe.Misses);
        Assert.False(probe.TryGetOffset(out _, out _));
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
        Assert.False(probe.IsUnsupported);
        Assert.False(probe.TryGetOffset(out _, out _));
    }
}
