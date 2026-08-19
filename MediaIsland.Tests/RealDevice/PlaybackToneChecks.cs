using MediaIsland.Services.Audio;
using MediaIsland.Services.Audio.Native;
using MediaIsland.Services.Audio.Playback;
using MediaIsland.Services.Audio.Playback.Native;
using MediaIsland.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace MediaIsland.Tests.RealDevice;

/// <summary>
/// 播放路径的真机主判据。覆盖 AGENTS.md 的条 1（出声且是同一个声音）、
/// 条 3（设备采样率与重采样）、条 4（起播无爆音）、条 9（已播出帧回调链）。
///
/// 第 4 期交付了整条播放路径，而 render.rs 的 render_frames 循环没有任何自动化覆盖，
/// 其中四处结构性修复经变异实测确认撤销后套件全绿。这个文件是那件事的补救。
///
/// 四条 AGENTS 条目合在一个测试里，理由是成本而非偷懒：端点是独占资源必须串行，
/// 每条判据都要三秒起播与采集，分成四个测试就是四次十秒级的等待，
/// 而它们看的本就是同一段采回信号的不同侧面。每个断言标注它对应哪条。
/// </summary>
[Collection(nameof(RealDeviceCollection))]
public class PlaybackToneChecks(ITestOutputHelper output)
{
    private const double ToneHz = 1_000;
    private const double Amplitude = 0.5;
    private const int TargetBufferMs = 200;

    [RealAudioFact]
    public async Task InjectedTone_IsPlayedBackAtTheSameFrequency()
    {
        AudioRenderNative.ResetForTesting();
        AudioCaptureNative.ResetForTesting();

        using var renderer = new WasapiRenderer();
        Assert.True(renderer.IsAvailable, $"播放不可用：{renderer.FailureReason}");

        using var loopback = new WasapiLoopbackFrameSource();
        Assert.True(loopback.IsAvailable, $"采集不可用：{loopback.FailureReason}");

        var played = new PlayedFrameRecorder();
        using var playback = new AudioPlaybackService(played, renderer);
        using var hub = new AudioFrameHub(loopback);
        var captured = new PcmAccumulator();
        using var subscription = hub.AddSink(captured);

        playback.Configure(enabled: true, targetBufferMs: TargetBufferMs);
        Assert.True(playback.IsPlaying, $"起播失败：{playback.LastError}");

        await hub.SetCaptureDemandAsync(true, CancellationToken.None);
        Assert.True(hub.IsCapturing, "loopback 采集没起来");

        using var sine = new SineFrameSource(ToneHz, Amplitude);
        sine.FrameAvailable += playback.Submit;
        await sine.StartAsync(CancellationToken.None);

        await Task.Delay(3_000);

        await sine.StopAsync(CancellationToken.None);
        await hub.SetCaptureDemandAsync(false, CancellationToken.None);

        // 统计要在停播之前读：停播不清统计（那是刻意的），但缓冲占用会归零，
        // 而占用是这里唯一有意义的瞬时量。
        var stats = renderer.ReadStats();
        playback.Configure(enabled: false, targetBufferMs: TargetBufferMs);

        var pcm = captured.ToArray();
        var tone = ToneAnalysis.Analyze(pcm, captured.SampleRate, captured.Channels);
        var bound = ToneAnalysis.MaxStepBoundFor(Amplitude * short.MaxValue, ToneHz, 48_000);
        var step = ToneAnalysis.MaxAbsoluteStep(pcm, captured.Channels);

        // 数值一律留痕。承第 4 期的教训：缺口清单不等于缺陷清单，
        // 「绿」是不是巧合，只能靠数值让下一个读的人自己判断。
        output.WriteLine($"注入块数      : {sine.TotalFramesEmitted}");
        output.WriteLine($"采回帧数      : {captured.FrameCount}（静音 {captured.SilentFrameCount}）");
        output.WriteLine($"采回字节      : {pcm.Length}");
        output.WriteLine($"设备采样率    : {stats.DeviceSampleRate}");
        output.WriteLine($"重采样比      : {stats.ResampleRatioPpm} ppm");
        output.WriteLine($"缓冲占用      : {stats.RingFrames} 帧 / {stats.RingMs:F1}ms");
        output.WriteLine($"已渲染        : {stats.DeviceFramesRendered} 帧 / {stats.RenderedMs:F0}ms");
        output.WriteLine($"欠载 / 硬重置 : {stats.UnderrunCount} / {stats.HardResetCount}");
        output.WriteLine($"谱峰          : {tone.PeakHz:F1}Hz，信噪比 {tone.SignalToNoise:F1}");
        output.WriteLine($"最大跳变      : {step}（上界 {bound:F1}）");
        output.WriteLine($"已播出        : {played.Count} 帧，峰值 {played.Peak}");

        // 地基。不钉这一条，「采回为空」会让下面每一条判据都恰好满足。
        Assert.True(captured.FrameCount > 0, "loopback 一帧都没采到");
        Assert.Equal(48_000, captured.SampleRate);
        Assert.Equal(2, captured.Channels);

        // 条 3：设备采样率与重采样比必须自洽。基准比率是 设备率/48000，
        // 控制律的漂移项最多偏一个百分点（MAX_RELATIVE_RATIO），故容差取两个百分点。
        // 这一条对任何端点采样率都成立——覆盖「两个分支」靠换端点重跑，判据不用改。
        Assert.True(stats.HasStarted, "统计说从未起播，而 IsPlaying 曾为真");
        var expectedPpm = (long)Math.Round(stats.DeviceSampleRate * 1_000_000.0 / 48_000);
        Assert.InRange(stats.ResampleRatioPpm, expectedPpm - 20_000, expectedPpm + 20_000);

        // 条 1：端点确实在响，且响的是注入的那个频率。
        var binWidth = 48_000.0 / ToneAnalysis.FftLength;
        Assert.InRange(tone.PeakHz, ToneHz - 2 * binWidth, ToneHz + 2 * binWidth);
        Assert.True(
            tone.SignalToNoise > 10,
            $"信噪比仅 {tone.SignalToNoise:F1}：要么播放没真的出声，要么端点上还有别的声音在响");

        // 条 4：起播与全程都无爆音。
        Assert.True(
            step <= bound * 3,
            $"最大跳变 {step} 超出理论上界 {bound:F1} 的三倍：波形上有不连续");

        // 条 9：已播出帧回调链。变异实测过：回调整个不触发时红在 Peak 而不是 Count，
        // 因为预填充分支仍在 emit 静音块——故两条断言各有独立价值。
        Assert.True(played.Count > 0, "FramePlayed 一次都没触发：播放时可视化会冻结");
        Assert.True(played.Peak > 0, "已播出帧全是零");
        Assert.Equal(48_000, played.SampleRate);
        Assert.Equal(2, played.Channels);

        // 已播出的块不得报静音。变异实测发现这一格原先无判据：把 IsSilent 改成恒真，
        // 上面四条全绿。而它的后果是真的——可视化据此跳过 FFT，频谱会冻结在上一帧
        // 波形上而非跟随声音，那正是采集侧「静音填零而非跳过」要避免的形态。
        //
        // 播放侧 IsSilent 应恒假：已播出的块即使内容全零也代表「这一刻扬声器在按
        // 时间轴前进」，静音是上游给的语义，播放侧无从判断也不该猜。
        Assert.Equal(0, played.SilentCount);
    }

    /// <summary>
    /// 收 <c>FramePlayed</c> 转来的帧。装饰器把已播出的一块喂给内层，
    /// 故挂在内层的位置上就能观测整条回调链。
    /// </summary>
    private sealed class PlayedFrameRecorder : IAudioFrameSubmitter
    {
        private readonly object _gate = new();

        public int Count { get; private set; }

        public int Peak { get; private set; }

        public int SampleRate { get; private set; }

        public int Channels { get; private set; }

        /// <summary>报了静音的块数。播放侧应恒为零，理由见调用处的断言。</summary>
        public int SilentCount { get; private set; }

        public void Submit(AudioFrame frame)
        {
            lock (_gate)
            {
                Count++;
                SampleRate = frame.SampleRate;
                Channels = frame.Channels;
                if (frame.IsSilent)
                {
                    SilentCount++;
                }

                for (var i = 0; i + 1 < frame.Pcm.Length; i += 2)
                {
                    var sample = Math.Abs(BitConverter.ToInt16(frame.Pcm, i));
                    if (sample > Peak)
                    {
                        Peak = sample;
                    }
                }
            }
        }
    }
}
