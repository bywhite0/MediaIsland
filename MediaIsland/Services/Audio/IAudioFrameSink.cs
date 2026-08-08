namespace MediaIsland.Services.Audio;

/// <summary>
/// PCM 帧汇。实现可以是 MediaLink 广播、FFT 分析（第 3 期）、播放（第 4 期）。
///
/// 用 <c>in</c> 传 <see cref="AudioFrame"/> 避免每帧复制结构体：50 帧/秒 × 多个 sink，
/// 复制本身不贵，但没有理由付这笔钱。
/// </summary>
public interface IAudioFrameSink
{
    ValueTask OnFrameAsync(in AudioFrame frame, CancellationToken cancellationToken);
}
