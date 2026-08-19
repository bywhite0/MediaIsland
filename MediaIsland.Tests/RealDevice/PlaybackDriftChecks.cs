using System.Diagnostics;
using MediaIsland.Services.Audio;
using MediaIsland.Services.Audio.Playback;
using MediaIsland.Services.Audio.Playback.Native;
using MediaIsland.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace MediaIsland.Tests.RealDevice;

/// <summary>
/// 漂移与欠载处置的真机判据。覆盖 AGENTS.md 的条 5（长时漂移）与
/// 条 6a（连续欠载只硬重置一次）。
///
/// 这两条是统计 FFI 存在的主要理由：前者此前只能等到破音，后者在外部完全不可观测——
/// 一次硬重置与几次连续欠载，在采回的 PCM 上长得一样。
///
/// 关于漂移控制律的一条要紧前提：<c>build_resampler</c> 在设备率恰为 48000 时返回
/// None，故 48k 端点上不建重采样器，而重采样比正是漂移调节的唯一载体——
/// 没有载体就没有调节。所以在 48k 端点上，本文件第一条测的是「注入速率与设备时钟的
/// 自然匹配度」，不是控制律；控制律的符号只能在非 48k 端点上验证，
/// 那一项归入 AGENTS.md 的换端点重跑。测试会把这一点打印出来，避免读者误判。
/// </summary>
[Collection(nameof(RealDeviceCollection))]
public class PlaybackDriftChecks(ITestOutputHelper output)
{
    private const int TargetBufferMs = 200;
    private const int TargetFrames = 48_000 * TargetBufferMs / 1_000;

    /// <summary>观测时长。可由环境变量拉长，用于偶尔的长跑复核。</summary>
    private static int ObserveSeconds =>
        int.TryParse(Environment.GetEnvironmentVariable("MEDIAISLAND_DRIFT_SECONDS"), out var s)
            ? Math.Clamp(s, 10, 900)
            : 20;

    [RealAudioFact]
    public async Task BufferOccupancy_DoesNotWalkTowardsEmptyOrFull()
    {
        AudioRenderNative.ResetForTesting();

        using var renderer = new WasapiRenderer();
        Assert.True(renderer.IsAvailable, $"播放不可用：{renderer.FailureReason}");

        using var playback = new AudioPlaybackService(new NullSubmitter(), renderer);
        playback.Configure(enabled: true, targetBufferMs: TargetBufferMs);
        Assert.True(playback.IsPlaying, $"起播失败：{playback.LastError}");

        using var sine = new SineFrameSource(frequencyHz: 1_000, amplitude: 0.5);
        sine.FrameAvailable += playback.Submit;
        await sine.StartAsync(CancellationToken.None);

        // 先让预填充与起播过渡走完再开始采样：起播那一段占用从 0 爬到目标深度，
        // 把它算进斜率会得到一个巨大的正值，而那不是漂移。
        await Task.Delay(2_000);

        var samples = new List<(double Seconds, long Frames)>();
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < ObserveSeconds)
        {
            var probe = renderer.ReadStats();
            samples.Add((clock.Elapsed.TotalSeconds, probe.RingFrames));
            await Task.Delay(100);
        }

        var final = renderer.ReadStats();
        await sine.StopAsync(CancellationToken.None);
        playback.Configure(enabled: false, targetBufferMs: TargetBufferMs);

        var slope = BufferTrend.SlopeFramesPerSecond(samples);
        var mean = samples.Average(s => (double)s.Frames);
        var min = samples.Min(s => s.Frames);
        var max = samples.Max(s => s.Frames);
        var resamplingEngaged = final.DeviceSampleRate != 48_000;

        output.WriteLine($"观测时长      : {ObserveSeconds}s，样本 {samples.Count} 个");
        output.WriteLine($"设备采样率    : {final.DeviceSampleRate}");
        output.WriteLine($"重采样比      : {final.ResampleRatioPpm} ppm");
        output.WriteLine(
            $"重采样参与    : {(resamplingEngaged ? "是，控制律在调节" : "否（48k 端点），控制律不参与")}");
        output.WriteLine($"占用 均/最小/最大 : {mean:F0} / {min} / {max} 帧（目标 {TargetFrames}）");
        output.WriteLine($"斜率          : {slope:F1} 帧/秒");
        output.WriteLine($"欠载 / 硬重置 : {final.UnderrunCount} / {final.HardResetCount}");
        output.WriteLine($"已渲染        : {final.RenderedMs:F0}ms（墙钟约 {(ObserveSeconds + 2) * 1_000}ms）");

        // 地基三条。缺任何一条，下面的斜率判据都可能因为「根本没在跑」而恰好满足。
        Assert.True(samples.Count >= 10, $"只采到 {samples.Count} 个样本");
        Assert.True(final.HasStarted, "统计说从未起播");
        Assert.True(mean > 0, "占用全程为零：帧没进到环形缓冲");

        // 条 5。阈值从物理量推而不取魔数：若斜率的绝对值大到能在观测时长的一半内
        // 把缓冲走空或走满，那无论有没有控制律，这条流都撑不住。
        var bound = TargetFrames / (ObserveSeconds / 2.0);
        Assert.True(
            Math.Abs(slope) < bound,
            $"占用斜率 {slope:F1} 帧/秒超过 {bound:F1}：缓冲在单调走向空或满" +
            (resamplingEngaged ? "，控制律的符号可能反了" : "（本端点无控制律，故这是注入或设备时钟的问题）"));

        // 注入速率追着墙钟，正常情况下不该欠载。欠载不为零不必然是缺陷
        // （调度抖动会造成偶发），但硬重置意味着连续欠载累积过了 500ms。
        Assert.Equal(0, final.HardResetCount);
    }

    [RealAudioFact]
    public async Task SustainedUnderrun_TriggersExactlyOneHardReset()
    {
        AudioRenderNative.ResetForTesting();

        using var renderer = new WasapiRenderer();
        Assert.True(renderer.IsAvailable, $"播放不可用：{renderer.FailureReason}");

        using var playback = new AudioPlaybackService(new NullSubmitter(), renderer);
        playback.Configure(enabled: true, targetBufferMs: TargetBufferMs);
        Assert.True(playback.IsPlaying, $"起播失败：{playback.LastError}");

        using var sine = new SineFrameSource(frequencyHz: 1_000, amplitude: 0.5);
        sine.FrameAvailable += playback.Submit;
        await sine.StartAsync(CancellationToken.None);
        await Task.Delay(2_000);

        var beforeStall = renderer.ReadStats();
        Assert.Equal(0, beforeStall.HardResetCount);

        // 停止注入。缓冲走空后每一轮都取不够帧，欠载累积过 UNDERRUN_RESET_MS（500ms）
        // 触发一次硬重置，此后闸门关闭、重新预填充——而没有新数据进来，
        // 于是它应当停在一次，不该反复重置。停三秒，远超 500ms 与预填充时长之和。
        //
        // 这是「拔线」那条手工项里唯一能自动化的一半：欠载走的是 note_underrun，
        // 端点消失走的是 WASAPI 错误返回，两条路径不同。
        await sine.StopAsync(CancellationToken.None);
        await Task.Delay(3_000);

        var afterStall = renderer.ReadStats();

        // 恢复注入，确认能重新起来。
        using var resumed = new SineFrameSource(frequencyHz: 1_000, amplitude: 0.5);
        resumed.FrameAvailable += playback.Submit;
        await resumed.StartAsync(CancellationToken.None);
        await Task.Delay(2_000);

        var afterResume = renderer.ReadStats();
        await resumed.StopAsync(CancellationToken.None);
        playback.Configure(enabled: false, targetBufferMs: TargetBufferMs);

        output.WriteLine($"停注入前 欠载/重置 : {beforeStall.UnderrunCount} / {beforeStall.HardResetCount}");
        output.WriteLine($"停注入后 欠载/重置 : {afterStall.UnderrunCount} / {afterStall.HardResetCount}");
        output.WriteLine($"恢复后   欠载/重置 : {afterResume.UnderrunCount} / {afterResume.HardResetCount}");
        output.WriteLine($"恢复后   占用       : {afterResume.RingFrames} 帧 / {afterResume.RingMs:F1}ms");

        // 地基：欠载真的发生了。不钉这一条，「一次都没欠载」会让下面那条
        // 「只重置一次」变成恒真。
        Assert.True(
            afterStall.UnderrunCount > beforeStall.UnderrunCount,
            "停止注入三秒却没有欠载：帧可能根本没在流动");

        // 条 6a 的核心：连续欠载只触发一次硬重置，不反复重置。
        Assert.Equal(1, afterStall.HardResetCount);

        // 而「只重置一次」这一条单独不够——变异实测发现，把 note_underrun 改成
        // 每次欠载都重置，计数会变成「欠载 1、重置 1」而不是「欠载 50、重置 1」：
        // 第一次欠载就重置，闸门关闭进入预填充，而预填充期不计欠载，
        // 又没有新数据可填，于是永远停在那里、不再欠载也不再重置。两种行为下
        // HardResetCount 都恰好是 1。
        //
        // 真正的契约是「多次欠载才换来一次重置」，故必须钉住比例。
        // UNDERRUN_RESET_MS 是 500ms，而每轮欠载缺的是一个缓冲周期的量级（约 10ms），
        // 故正确实现下第一次重置前至少要累积十几次欠载。取 10 作安全下界。
        Assert.True(
            afterStall.UnderrunCount >= 10,
            $"只欠载了 {afterStall.UnderrunCount} 次就重置：" +
            "重置的门槛可能失效了，每次欠载都在重置");

        // 恢复注入后不该再重置，且缓冲要重新蓄起来。
        Assert.Equal(1, afterResume.HardResetCount);
        Assert.True(
            afterResume.RingFrames > 0,
            "恢复注入后缓冲仍为空：硬重置之后没能重新起来");
    }

    /// <summary>丢弃帧。本文件的判据全部读自统计，不需要观测已播出的 PCM。</summary>
    private sealed class NullSubmitter : IAudioFrameSubmitter
    {
        public void Submit(AudioFrame frame)
        {
        }
    }
}
