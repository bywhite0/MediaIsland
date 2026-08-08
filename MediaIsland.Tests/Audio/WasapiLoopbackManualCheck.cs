using System.Diagnostics;
using MediaIsland.Services.Audio;
using MediaIsland.Services.Audio.Native;
using Xunit;
using Xunit.Abstractions;

namespace MediaIsland.Tests.Audio;

/// <summary>
/// 真机 WASAPI loopback 采集验证。**默认跳过**——它需要真实音频设备且正在放音，
/// 在 CI 上必然失败。手工验证时把 Skip 去掉再跑。
///
/// 这是本期唯一无法自动化的一环：WASAPI 依赖真机，其余全部由单测覆盖。
/// </summary>
[Collection(nameof(AudioNativeCollection))]
public class WasapiLoopbackManualCheck(ITestOutputHelper output)
{
    [Fact(Skip = "手工验证：需真实音频设备且正在放音。见 AGENTS.md 的 MediaLink Audio Capture Check")]
    public async Task CapturesRealLoopbackAudio()
    {
        AudioCaptureNative.ResetForTesting();
        using var source = new WasapiLoopbackFrameSource();
        output.WriteLine($"IsAvailable = {source.IsAvailable}");
        output.WriteLine($"FailureReason = {source.FailureReason ?? "(无)"}");
        Assert.True(source.IsAvailable, source.FailureReason);

        using var hub = new AudioFrameHub(source);
        var sink = new StatsSink();
        using var _ = hub.AddSink(sink);

        await hub.SetCaptureDemandAsync(true, CancellationToken.None);
        Assert.True(hub.IsCapturing);

        var sw = Stopwatch.StartNew();
        await Task.Delay(3000);
        sw.Stop();

        await hub.SetCaptureDemandAsync(false, CancellationToken.None);
        Assert.False(hub.IsCapturing);

        output.WriteLine($"帧数        : {sink.Count}");
        output.WriteLine($"帧率        : {sink.Count / sw.Elapsed.TotalSeconds:F1} 帧/秒");
        output.WriteLine($"总时长      : {sink.TotalDurationMs:F0} ms (墙钟 {sw.Elapsed.TotalMilliseconds:F0} ms)");
        output.WriteLine($"采样率/声道 : {sink.SampleRate} / {sink.Channels}");
        output.WriteLine($"PCM 字节/帧 : {sink.MinBytes}..{sink.MaxBytes}");
        output.WriteLine($"静音帧      : {sink.SilentCount}");
        output.WriteLine($"非零样本帧  : {sink.NonZeroCount}");
        output.WriteLine($"峰值振幅    : {sink.Peak} / 32767");

        Assert.True(sink.Count > 0, "未收到任何帧");
        Assert.Equal(48_000, sink.SampleRate);
        Assert.Equal(2, sink.Channels);

        // 采集时长应逼近墙钟：偏差过大说明分帧或重采样把时间轴拉伸了。
        var drift = Math.Abs(sink.TotalDurationMs - sw.Elapsed.TotalMilliseconds);
        Assert.True(drift < 500, $"时长漂移 {drift:F0} ms 过大");
    }

    private sealed class StatsSink : IAudioFrameSink
    {
        public int Count, SilentCount, NonZeroCount, SampleRate, Channels;
        public int MinBytes = int.MaxValue, MaxBytes;
        public double TotalDurationMs;
        public short Peak;

        public ValueTask OnFrameAsync(AudioFrame frame, CancellationToken cancellationToken)
        {
            Count++;
            SampleRate = frame.SampleRate;
            Channels = frame.Channels;
            MinBytes = Math.Min(MinBytes, frame.Pcm.Length);
            MaxBytes = Math.Max(MaxBytes, frame.Pcm.Length);
            TotalDurationMs += frame.DurationMs;
            if (frame.IsSilent)
            {
                SilentCount++;
            }

            var any = false;
            for (var i = 0; i + 1 < frame.Pcm.Length; i += 2)
            {
                var sample = (short)(frame.Pcm[i] | (frame.Pcm[i + 1] << 8));
                var amplitude = sample == short.MinValue ? short.MaxValue : Math.Abs(sample);
                if (amplitude > Peak)
                {
                    Peak = (short)amplitude;
                }

                if (sample != 0)
                {
                    any = true;
                }
            }

            if (any)
            {
                NonZeroCount++;
            }

            return ValueTask.CompletedTask;
        }
    }
}
