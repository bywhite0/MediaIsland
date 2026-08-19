using MediaIsland.Services.Audio;
using MediaIsland.Services.Audio.Native;
using MediaIsland.Services.Audio.Playback;
using MediaIsland.Services.Audio.Playback.Native;
using MediaIsland.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace MediaIsland.Tests.RealDevice;

/// <summary>
/// 播放的启停与重配。覆盖 AGENTS.md 条 10（反复开关）、条 11（深度改动重启）、
/// 条 13（干净关停）。
///
/// 这三条的共同点是它们都在验状态转移而非稳态，而第 4 期的自动化只覆盖了两侧稳态
/// 与两条写者路径的串行化。
///
/// 条 14（native 库缺失的降级）不进本文件：`WasapiRendererDegradationTests` 与
/// `AudioPlaybackServiceTests.UnavailableRenderer_FallsBackToDirect` 已完整覆盖，
/// 而在测试进程里改 DLL 名会污染同进程其他测试——`AudioRenderNative` 的探测结果
/// 是静态缓存的。真机重命名唯一多验的是 LibraryImport 的解析会失败，
/// 那是运行时行为，不是本仓逻辑。
/// </summary>
[Collection(nameof(RealDeviceCollection))]
public class PlaybackLifecycleChecks(ITestOutputHelper output)
{
    [RealAudioFact]
    public async Task RepeatedToggling_RecoversToneEveryTime()
    {
        // 条 10。开关三轮，每轮都要重新出声——一次成功不能说明切换接缝没问题，
        // 而反复切换正是暴露「停播没停干净」「起播抢不到设备」的场景。
        AudioRenderNative.ResetForTesting();
        AudioCaptureNative.ResetForTesting();

        using var renderer = new WasapiRenderer();
        Assert.True(renderer.IsAvailable, $"播放不可用：{renderer.FailureReason}");

        using var loopback = new WasapiLoopbackFrameSource();
        Assert.True(loopback.IsAvailable, $"采集不可用：{loopback.FailureReason}");

        using var playback = new AudioPlaybackService(new NullSubmitter(), renderer);
        var binWidth = 48_000.0 / ToneAnalysis.FftLength;

        for (var round = 1; round <= 3; round++)
        {
            playback.Configure(enabled: true, targetBufferMs: 200);
            Assert.True(playback.IsPlaying, $"第 {round} 轮起播失败：{playback.LastError}");

            using var hub = new AudioFrameHub(loopback);
            var captured = new PcmAccumulator();
            using var subscription = hub.AddSink(captured);
            await hub.SetCaptureDemandAsync(true, CancellationToken.None);

            using var sine = new SineFrameSource(frequencyHz: 1_000, amplitude: 0.5);
            sine.FrameAvailable += playback.Submit;
            await sine.StartAsync(CancellationToken.None);
            await Task.Delay(2_500);
            await sine.StopAsync(CancellationToken.None);
            await hub.SetCaptureDemandAsync(false, CancellationToken.None);

            var stats = renderer.ReadStats();
            playback.Configure(enabled: false, targetBufferMs: 200);
            Assert.False(playback.IsPlaying, $"第 {round} 轮停播后仍报在播放");

            var tone = ToneAnalysis.Analyze(captured.ToArray(), captured.SampleRate, captured.Channels);
            output.WriteLine(
                $"第 {round} 轮：谱峰 {tone.PeakHz:F1}Hz，信噪比 {tone.SignalToNoise:F1}，" +
                $"欠载/重置 {stats.UnderrunCount}/{stats.HardResetCount}，" +
                $"已渲染 {stats.RenderedMs:F0}ms");

            Assert.True(captured.FrameCount > 0, $"第 {round} 轮一帧都没采到");
            Assert.InRange(tone.PeakHz, 1_000 - 2 * binWidth, 1_000 + 2 * binWidth);
            Assert.True(
                tone.SignalToNoise > 10,
                $"第 {round} 轮信噪比仅 {tone.SignalToNoise:F1}：这一轮没能重新出声");

            // 每轮起播都是新会话，故统计从零开始。第二轮起若计数带着上一轮的值，
            // 说明 start 时没有 reset——那会让「本次播放共欠载几次」变成历史累计。
            Assert.Equal(0, stats.HardResetCount);
        }
    }

    [RealAudioFact]
    public async Task ChangingBufferDepth_RestartsWithAFreshSession()
    {
        // 条 11。深度变更走停播重启（不做在线调整），故判据是「新会话」：
        // 统计被清零、设备率被重新写入、缓冲按新深度蓄起来。
        AudioRenderNative.ResetForTesting();

        using var renderer = new WasapiRenderer();
        Assert.True(renderer.IsAvailable, $"播放不可用：{renderer.FailureReason}");

        using var playback = new AudioPlaybackService(new NullSubmitter(), renderer);
        using var sine = new SineFrameSource(frequencyHz: 1_000, amplitude: 0.5);
        sine.FrameAvailable += playback.Submit;
        await sine.StartAsync(CancellationToken.None);

        playback.Configure(enabled: true, targetBufferMs: 200);
        Assert.True(playback.IsPlaying, $"起播失败：{playback.LastError}");
        await Task.Delay(2_500);
        var shallow = renderer.ReadStats();

        // 换深度。Configure 内部会 Stop 再 Start。
        playback.Configure(enabled: true, targetBufferMs: 600);
        Assert.True(playback.IsPlaying, $"换深度后重启失败：{playback.LastError}");
        await Task.Delay(3_500);
        var deep = renderer.ReadStats();

        await sine.StopAsync(CancellationToken.None);
        playback.Configure(enabled: false, targetBufferMs: 600);

        output.WriteLine($"200ms 深度：占用 {shallow.RingFrames} 帧 / {shallow.RingMs:F1}ms，已渲染 {shallow.RenderedMs:F0}ms");
        output.WriteLine($"600ms 深度：占用 {deep.RingFrames} 帧 / {deep.RingMs:F1}ms，已渲染 {deep.RenderedMs:F0}ms");
        output.WriteLine($"请求深度：{playback.RequestedTargetBufferMs}");

        // 地基：两次都真的在跑。
        Assert.True(shallow.HasStarted && deep.HasStarted);
        Assert.True(shallow.RingFrames > 0 && deep.RingFrames > 0);

        // 新会话的证据：已渲染时长在 start 时被清零后重新累积，故第二段的读数只应
        // 反映第二段自己的时长（约 3500ms 的等待），与第一段无关。
        //
        // 阈值不能写成 shallow.RenderedMs + 3500 + 余量——变异实测栽在这上面：
        // 那个式子把被检测量本身加进了容许范围，恰好抵消了要检测的差异。
        // 删掉 stats.reset() 后读数变成 6064ms（两段之和），而那个式子容许到 7032ms，
        // 判据全绿。正确的界只与第二段有关。
        const double secondSegmentMs = 3_500;
        Assert.True(
            deep.RenderedMs < secondSegmentMs + 1_500,
            $"换深度后已渲染时长是 {deep.RenderedMs:F0}ms，超过第二段自己的 {secondSegmentMs}ms 加余量："
            + $"看起来带着上一段的 {shallow.RenderedMs:F0}ms，start 没有清零统计");

        // 深度确实变深了。600ms 的目标下占用应明显高于 200ms 的目标。
        Assert.True(
            deep.RingMs > shallow.RingMs * 1.5,
            $"深度从 200ms 调到 600ms，占用只从 {shallow.RingMs:F0}ms 变到 {deep.RingMs:F0}ms");
    }

    [RealAudioFact]
    public async Task Disposing_WhilePlaying_ReleasesTheEndpoint()
    {
        // 条 13。判据不是「进程没挂」——那太弱，挂起表现为测试超时而非失败信息。
        // 判据是：Dispose 之后统计读为全零（句柄已销毁），且紧接着能再建一个渲染器
        // 并成功起播出声——端点真的被释放了才可能。
        AudioRenderNative.ResetForTesting();

        var renderer = new WasapiRenderer();
        Assert.True(renderer.IsAvailable, $"播放不可用：{renderer.FailureReason}");

        var playback = new AudioPlaybackService(new NullSubmitter(), renderer);
        using var sine = new SineFrameSource(frequencyHz: 1_000, amplitude: 0.5);
        sine.FrameAvailable += playback.Submit;
        await sine.StartAsync(CancellationToken.None);

        playback.Configure(enabled: true, targetBufferMs: 200);
        Assert.True(playback.IsPlaying, $"起播失败：{playback.LastError}");
        await Task.Delay(2_000);

        var beforeDispose = renderer.ReadStats();
        Assert.True(beforeDispose.HasStarted);

        // 播放中直接 Dispose——不先 Stop。这是插件被卸载时的真实路径，
        // 而 native 的 destroy 内含 stop 并同步等渲染线程退出。
        playback.Dispose();
        renderer.Dispose();

        var afterDispose = renderer.ReadStats();
        output.WriteLine($"Dispose 前：已渲染 {beforeDispose.RenderedMs:F0}ms，设备率 {beforeDispose.DeviceSampleRate}");
        output.WriteLine($"Dispose 后：设备率 {afterDispose.DeviceSampleRate}（应为 0，句柄已销毁）");

        // 这一条守的是「托管侧不再拿已销毁的句柄去读」——读已释放的 Box 是 UB。
        // 它不能证明 native 侧真的销毁了：变异实测确认，只删掉 RenderDestroy 而保留
        // 句柄字段归零时，本条与下面那条都仍然绿。native 句柄与渲染线程的泄漏
        // 在托管侧不可观测，这条判据的强度到此为止，已记为已知缺口。
        Assert.False(
            afterDispose.HasStarted,
            "Dispose 后统计仍报起播过：句柄字段没归零，此后的读会打在已释放的 Box 上");

        // 端点可被重新占用。注意共享模式下即使上一个渲染流没退干净也通常能起来，
        // 故这一条同样不是「已释放」的充分证据；它守的是「重建路径本身可用」。
        using var second = new WasapiRenderer();
        using var secondPlayback = new AudioPlaybackService(new NullSubmitter(), second);
        using var secondSine = new SineFrameSource(frequencyHz: 1_000, amplitude: 0.5);
        secondSine.FrameAvailable += secondPlayback.Submit;
        await secondSine.StartAsync(CancellationToken.None);

        secondPlayback.Configure(enabled: true, targetBufferMs: 200);
        Assert.True(secondPlayback.IsPlaying, $"重建后起播失败：{secondPlayback.LastError}");
        await Task.Delay(2_000);

        var restarted = second.ReadStats();
        await secondSine.StopAsync(CancellationToken.None);
        secondPlayback.Configure(enabled: false, targetBufferMs: 200);

        output.WriteLine($"重建后：已渲染 {restarted.RenderedMs:F0}ms，欠载/重置 {restarted.UnderrunCount}/{restarted.HardResetCount}");

        Assert.True(
            restarted.RenderedMs > 500,
            $"重建后只渲染了 {restarted.RenderedMs:F0}ms：上一个渲染线程可能没退干净");
    }

    private sealed class NullSubmitter : IAudioFrameSubmitter
    {
        public void Submit(AudioFrame frame)
        {
        }
    }
}
