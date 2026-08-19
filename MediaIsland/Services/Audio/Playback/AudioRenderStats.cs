namespace MediaIsland.Services.Audio.Playback;

/// <summary>
/// 播放器的运行时统计。
///
/// 存在的理由是可观测性。渲染线程内部的缓冲占用、欠载与硬重置次数、重采样比此前一个
/// 都读不到，于是「长时播放不漂移」这类契约只能等到破音才判定——而那是人耳判据的形态，
/// 人只能等到破音才知道控制律的符号错了。占用可读时判的是斜率。
///
/// 「连续欠载只硬重置一次」在外部则完全不可观测：一次硬重置与几次连续欠载在采回的
/// PCM 上长得一样，两个计数分开才判得出「一次」。
///
/// 六个字段不是同一瞬间的快照，可能跨越一次渲染轮次——统计量在 native 侧是各自独立的
/// 原子量，刻意不去抢渲染线程等的那把互斥量。判据应看斜率与累计计数的单调性，
/// 不要依赖六元组的瞬时一致性。
/// </summary>
/// <param name="RingFrames">环形缓冲当前占用，48000Hz 域的帧数。</param>
/// <param name="UnderrunCount">欠载累计次数。</param>
/// <param name="HardResetCount">硬重置累计次数。</param>
/// <param name="DeviceFramesRendered">已写进设备缓冲的设备帧数。</param>
/// <param name="DeviceSampleRate">设备混音采样率。为 0 表示从未起播过。</param>
/// <param name="ResampleRatioPpm">当前重采样比，百万分之一。为 0 表示不可用。</param>
public readonly record struct AudioRenderStats(
    long RingFrames,
    long UnderrunCount,
    long HardResetCount,
    long DeviceFramesRendered,
    int DeviceSampleRate,
    long ResampleRatioPpm)
{
    /// <summary>本句柄起播过。为假时其余字段全为零。</summary>
    public bool HasStarted => DeviceSampleRate > 0;

    /// <summary>
    /// 缓冲占用的时长。
    ///
    /// 按传输采样率算而非设备采样率：环形缓冲存的是传输格式，重采样发生在它之后。
    /// 用设备率换算会在非 48k 设备上把占用算错，而占用是漂移判据的核心量。
    /// </summary>
    public double RingMs => RingFrames * 1_000.0 / 48_000;

    /// <summary>已渲染时长。按设备采样率算——那些是设备帧。</summary>
    public double RenderedMs =>
        DeviceSampleRate == 0 ? 0 : DeviceFramesRendered * 1_000.0 / DeviceSampleRate;
}
