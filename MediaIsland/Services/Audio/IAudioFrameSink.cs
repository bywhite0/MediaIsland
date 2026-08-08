namespace MediaIsland.Services.Audio;

/// <summary>
/// PCM 帧汇。实现可以是 MediaLink 广播、FFT 分析（第 3 期）、播放（第 4 期）。
///
/// 按值传 <see cref="AudioFrame"/> 而非 <c>in</c>：帧汇本质上是异步的（入队、发送），
/// 而 <c>in</c> 参数不允许出现在 async 方法上（CS1988），会把实现逼成同步或多包一层。
/// 结构体本身约 32 字节（数组引用 + 几个数值），50 帧/秒下的复制成本可忽略——
/// 为省这点开销而堵死异步实现是不划算的交易。
/// </summary>
public interface IAudioFrameSink
{
    ValueTask OnFrameAsync(AudioFrame frame, CancellationToken cancellationToken);
}
