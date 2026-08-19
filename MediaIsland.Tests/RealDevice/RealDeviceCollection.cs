using MediaIsland.Services.Audio;
using Xunit;

namespace MediaIsland.Tests.RealDevice;

/// <summary>
/// 真机验证集的 collection。
///
/// 必须禁并行：音频端点是进程级独占资源。两条测试同时持有渲染句柄时，
/// 第二条会拿到设备被占用的错误，而那与真缺陷无从区分——更坏的是它会随机发生。
/// 与既有 AudioNativeCollection 同理。
/// </summary>
[CollectionDefinition(nameof(RealDeviceCollection), DisableParallelization = true)]
public class RealDeviceCollection;

/// <summary>
/// 把收到的 PCM 全部攒起来，供 FFT 分析。
///
/// 攒而不是流式分析：谱峰判据要一整段连续样本，而帧是 20ms 一块的。
/// 三秒 48000Hz 立体声 i16 约 1.1MB，测试里可以接受。
/// </summary>
internal sealed class PcmAccumulator : IAudioFrameSink
{
    private readonly List<byte> _bytes = [];
    private readonly object _gate = new();

    public int SampleRate { get; private set; }

    public int Channels { get; private set; }

    public int FrameCount { get; private set; }

    public int SilentFrameCount { get; private set; }

    public ValueTask OnFrameAsync(AudioFrame frame, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            SampleRate = frame.SampleRate;
            Channels = frame.Channels;
            FrameCount++;
            if (frame.IsSilent)
            {
                SilentFrameCount++;
            }

            _bytes.AddRange(frame.Pcm);
        }

        return ValueTask.CompletedTask;
    }

    public byte[] ToArray()
    {
        lock (_gate)
        {
            return _bytes.ToArray();
        }
    }
}
