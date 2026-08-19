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
    /// 处理器在 native 渲染线程上被调用，且不得回调进 <see cref="Push"/>、
    /// <see cref="Stop"/> 或 <see cref="ReadStats"/>——三者共用同一把锁，
    /// 而停播路径上渲染线程正在被 join，处理器若在等那把锁就互等。
    /// </summary>
    event Action<AudioFrame>? FramePlayed;

    void Start(int targetBufferMs);

    void Stop();

    /// <summary>送一块 s16le 交错 PCM 进播放缓冲。未启动时静默忽略。</summary>
    void Push(byte[] pcm);

    /// <summary>
    /// 读运行时统计。native 不可用、句柄已释放或从未起播时返回全零
    /// （<see cref="AudioRenderStats.HasStarted"/> 为假）。
    ///
    /// 不返回可空值：调用方拿到全零与拿到 null 要做的判断相同，
    /// 多一种表示就多一处要处理的分支。
    ///
    /// 停播后仍可读到本次会话的累计值——句柄在停播后依然活着，只有释放才销毁。
    /// 这是刻意的：那些计数是停播后唯一还能读到的诊断信息。
    /// </summary>
    AudioRenderStats ReadStats();
}
