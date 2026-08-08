namespace MediaIsland.Services.Audio;

/// <summary>
/// PCM 帧源。实现可以是 native 采集、网络接收，或测试用的假源。
///
/// <see cref="IsAvailable"/> / <see cref="FailureReason"/> 沿用
/// <c>TtmlNativeParser</c> 的降级模式：native 库缺失或 ABI 不匹配时，
/// 采集静默不可用，而接收、转发、可视化这些纯托管路径照常工作。
/// 这让「只做接收端」在没有 native 库的平台上立即可用。
/// </summary>
public interface IAudioFrameSource
{
    bool IsAvailable { get; }

    string? FailureReason { get; }

    /// <summary>
    /// 采集到一块 PCM。**在采集线程上同步触发**，处理器不得阻塞——
    /// 阻塞会让 WASAPI 缓冲溢出并产生丢帧。
    /// </summary>
    event Action<AudioFrame>? FrameAvailable;

    Task StartAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}
