using System.Diagnostics;
using MediaIsland.Services.Audio;
using MediaIsland.Services.Audio.Playback;
using MediaIsland.Services.Audio.Playback.Native;
using MediaIsland.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace MediaIsland.Tests.RealDevice;

/// <summary>
/// 播放态下帧入口的实时性。覆盖 AGENTS.md 条 7 与条 8 里可自动化的那一半：
/// 播放与转发共存时互不干扰。
///
/// 为什么不搭三节点：那两条的实质已被第 4 期单测锁死——转发的字节相等由
/// AcceptedFrame_IsForwardedByteForByte 用 Assert.Same 钉住（同一个数组、无中间拷贝，
/// 比字节相等更强），真 socket 那一跳由 AudioFrame_OverRealWebSocket_ArrivesByteIdentical
/// 钉住，仲裁矩阵逐格锁死。再搭一套三节点真机测试验的是同一批东西。
///
/// 真机唯一无法被单测替代的是这个：Submit 在网络收循环里被调用，播放态下它走
/// WasapiRenderer.Push，那里持锁并做一次 memcpy，而对面等这把锁的是 WASAPI 实时线程。
/// Push 若偶发长阻塞，表现是转发抖动与入站积压，那与网络问题无从区分，
/// 而两者的排查方向相反。
/// </summary>
[Collection(nameof(RealDeviceCollection))]
public class PlaybackLatencyChecks(ITestOutputHelper output)
{
    private const int TargetBufferMs = 200;

    /// <summary>
    /// 单次 Submit 的容许上限。
    ///
    /// 帧间隔是 20ms，故 5ms 已经是四分之一个帧周期——超过它就意味着收循环在一帧
    /// 之内被占掉了可观的份额，而它还要收下一帧并转发给下游。
    /// 未争抢的 Monitor 是数十纳秒量级，正常播放中真正的争抢只来自罕见的启停。
    /// </summary>
    private const double MaxSubmitMs = 5;

    [RealAudioFact]
    public async Task Submit_DoesNotStallTheCallerWhilePlaying()
    {
        AudioRenderNative.ResetForTesting();

        using var renderer = new WasapiRenderer();
        Assert.True(renderer.IsAvailable, $"播放不可用：{renderer.FailureReason}");

        using var playback = new AudioPlaybackService(new NullSubmitter(), renderer);
        playback.Configure(enabled: true, targetBufferMs: TargetBufferMs);
        Assert.True(playback.IsPlaying, $"起播失败：{playback.LastError}");

        // 先让起播与预填充走完：那一段渲染线程正在建会话、抢设备，
        // 把它算进延迟分布会让阈值失去意义。
        using var sine = new SineFrameSource(frequencyHz: 1_000, amplitude: 0.5);
        var timings = new List<double>();
        var measuring = false;
        var gate = new object();

        // 计时只包 Submit 这一次调用，用 GetTimestamp 相减而非 new Stopwatch——
        // 被测量本身是微秒级，一次对象分配就占它百分之几。
        //
        // 由合成源的泵来喂帧，而不是自己写 Task.Delay 循环：Windows 的 Task.Delay
        // 实际粒度约 15.6ms，写 Delay(20) 得到的是约 31ms，即 32 块每秒而非 50，
        // 于是缓冲持续取不够，实测到 539 次欠载与真实的断续。那不影响延迟判据本身
        // （Push 只是入队加一次 memcpy，与缓冲状态无关），但它让这条测试在真机上
        // 制造 glitch，且数值会让读者以为播放有毛病。合成源的泵追墙钟，速率准确。
        sine.FrameAvailable += frame =>
        {
            var before = Stopwatch.GetTimestamp();
            playback.Submit(frame);
            var after = Stopwatch.GetTimestamp();

            if (!Volatile.Read(ref measuring))
            {
                return;
            }

            var elapsedMs = (after - before) * 1_000.0 / Stopwatch.Frequency;
            lock (gate)
            {
                timings.Add(elapsedMs);
            }
        };

        await sine.StartAsync(CancellationToken.None);
        await Task.Delay(2_000);

        Volatile.Write(ref measuring, true);
        await Task.Delay(10_000);
        Volatile.Write(ref measuring, false);

        var stats = renderer.ReadStats();
        await sine.StopAsync(CancellationToken.None);
        playback.Configure(enabled: false, targetBufferMs: TargetBufferMs);

        lock (gate)
        {
            timings.Sort();
        }

        Assert.True(timings.Count >= 100, $"十秒内只测到 {timings.Count} 次 Submit");

        var max = timings[^1];
        var p99 = timings[(int)(timings.Count * 0.99)];
        var median = timings[timings.Count / 2];

        output.WriteLine($"Submit 次数   : {timings.Count}");
        output.WriteLine($"中位 / p99 / 最大 : {median:F4} / {p99:F4} / {max:F4} ms");
        output.WriteLine($"欠载 / 硬重置 : {stats.UnderrunCount} / {stats.HardResetCount}");
        output.WriteLine($"占用          : {stats.RingFrames} 帧 / {stats.RingMs:F1}ms");
        output.WriteLine($"已渲染        : {stats.DeviceFramesRendered} 帧 / {stats.RenderedMs:F0}ms");

        // 地基：确实是在播放态下测的。若中途掉出播放态，Submit 会走直连的
        // NullSubmitter，那条路径几乎零耗时，判据会恰好满足。
        Assert.True(stats.HasStarted, "统计说从未起播");
        Assert.True(
            stats.DeviceFramesRendered > 0,
            "一帧都没渲染出去：Submit 可能全程走的是直连分支");

        // 稳态下缓冲该是有货的。这一条同时证明喂帧速率正常——若又退化成
        // 32 块每秒，占用会趋零而欠载暴涨。
        Assert.True(
            stats.RingFrames > 0,
            $"缓冲占用为零而欠载 {stats.UnderrunCount} 次：喂帧速率跟不上设备消耗");

        Assert.True(
            p99 < MaxSubmitMs,
            $"Submit 的 p99 达 {p99:F4}ms，超过 {MaxSubmitMs}ms：播放会拖慢收循环与转发");

        // 最大值单独判且放宽：单次 GC 或线程被抢占都会造成一个孤立的尖峰，
        // 那不是锁竞争。一个帧周期是它的界——超过它就真的丢了一帧的时间。
        Assert.True(
            max < SineFrameSource.FrameMs,
            $"Submit 最长阻塞 {max:F4}ms，超过一个帧周期 {SineFrameSource.FrameMs}ms");
    }

    /// <summary>丢弃帧。本文件的判据全部读自统计与计时，不需要观测已播出的 PCM。</summary>
    private sealed class NullSubmitter : IAudioFrameSubmitter
    {
        public void Submit(AudioFrame frame)
        {
        }
    }
}
