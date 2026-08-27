namespace MediaIsland.Services.Audio;

/// <summary>
/// 一块采集到的 PCM。恒为 48000Hz / 2 声道 / i16 小端交错——这是 MediaLink 声明的线格式，
/// 归一化在 native 侧完成，托管侧不再处理设备原始格式。
///
/// 刻意不带曲目位置：本层不知晓「曲目」这个概念，那是 MediaLink 的领域。
/// 这里只给出采样时刻的 QPC 读数，由上层结合 SMTC 插值位置推出曲目位置。
/// 边界切在「PCM 帧源」而非「loopback 实现」，故换平台（PipeWire / CoreAudio）时本类型不变。
/// </summary>
/// <param name="Pcm">i16 小端交错的裸 PCM。已是独立副本，可安全跨线程持有。</param>
/// <param name="QpcPosition100Ns">采样时刻的高精度计数器读数，100ns 单位，与 <c>Stopwatch</c> 同源。</param>
/// <param name="IsSilent">
/// 本块是静音。仍携带完整长度的零值 PCM 而非空数组——接收端据此可跳过 FFT 计算，
/// 但时间轴必须照常推进，否则可视化会冻结在最后一帧波形上而非归零。
/// </param>
/// <param name="SenderTimelineTicks100Ns">
/// 本块在发送端时间轴上的采样时刻，100ns 单位，基准是发送端的 Unix 墙钟（1970 起算）。
/// 0 表示无时刻——本机采集与老对端都落在这里，播放侧据此走不带时间轴的原路径。
///
/// 与 <paramref name="QpcPosition100Ns"/> 是两条不同的轴：那是本机单调钟，这是对端墙钟。
/// 两轴之间的映射由对时层估计，不在本类型的职责内；这里只负责把时刻原样带到播放侧。
/// </param>
public readonly record struct AudioFrame(
    byte[] Pcm,
    long QpcPosition100Ns,
    int SampleRate,
    int Channels,
    bool IsSilent,
    long SenderTimelineTicks100Ns = 0)
{
    /// <summary>每声道采样数。i16 故每样本 2 字节。</summary>
    public int FrameCount => Channels == 0 ? 0 : Pcm.Length / (Channels * sizeof(short));

    /// <summary>
    /// 本块的时长。按实际字节数算而非假定固定帧长——采集侧改帧长时，
    /// 写死 20ms 的下游会静默漂移而不报错。
    /// </summary>
    public double DurationMs =>
        SampleRate == 0 || Channels == 0
            ? 0
            : (double)FrameCount / SampleRate * 1000;
}
