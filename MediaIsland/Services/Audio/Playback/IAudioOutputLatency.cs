namespace MediaIsland.Services.Audio.Playback;

/// <summary>
/// 本机音频输出引入的延迟。呈现侧据此把视觉后移，与已经出声的那批帧对齐。
/// </summary>
/// <remarks>
/// 抽成接口而不是让组件直接引 <see cref="AudioPlaybackService"/>：组件只需要
/// 「延迟是多少」这一个量，不需要知道抖动缓冲、WASAPI 或渲染线程的存在。
/// 与 IEffectiveMediaSource 是同一条理由，也同样让呈现侧在没有播放层时仍可构造。
/// </remarks>
public interface IAudioOutputLatency
{
    /// <summary>
    /// 当前的输出延迟。不在出声时为 <see cref="System.TimeSpan.Zero"/>。
    ///
    /// 消费方必须按「零表示没有额外延迟」处理，而不是「不可用」——两者在这里
    /// 是同一件事，不需要区分。
    /// </summary>
    TimeSpan OutputLatency { get; }
}
