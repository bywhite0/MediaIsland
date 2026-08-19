namespace MediaIsland.Services.Audio.Playback;

/// <summary>
/// PCM 播放器。输入恒为 48000Hz / 2 声道 / i16 小端交错——与传输格式一致，
/// 设备格式的适配在 native 侧完成。
///
/// 与 <see cref="IAudioFrameSink"/> 的区别在于方向：sink 是「拿去处理」，renderer 是「拿去出声」，
/// 且它会反向吐出已播出的那一块，供可视化对齐到用户实际听到的内容。
/// </summary>
public interface IAudioRenderer : IDisposable
{
    bool IsAvailable { get; }

    string? FailureReason { get; }

    /// <summary>
    /// 已写进音频端点缓冲的一块 PCM。
    ///
    /// 写进缓冲不等于已出声：共享模式下还隔着约 10 到 30ms 的端点缓冲。
    /// 故用它驱动可视化的残余误差是那个量级，不是零。视觉滞后于听觉超过约 50ms
    /// 才可察觉，故可接受；要真正归零需要反查已呈现帧的位置，本期不做。
    ///
    /// 处理器在 native 渲染线程上被调用，且不得回调进 <see cref="Push"/> 或
    /// <see cref="Stop"/>——那会与停播路径互等。
    /// </summary>
    event Action<AudioFrame>? FramePlayed;

    void Start(int targetBufferMs);

    void Stop();

    /// <summary>送一块 s16le 交错 PCM 进播放缓冲。未启动时静默忽略。</summary>
    void Push(byte[] pcm);
}
