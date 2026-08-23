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
    /// 处理器在 native 渲染线程上被调用，且不得调用本接口上的任何方法——
    /// 这些方法共用同一把锁，而停播路径上渲染线程正在被 join，处理器若在等那把锁就互等。
    /// 两个只读属性（<see cref="IsAvailable"/>、<see cref="FailureReason"/>）可读：
    /// 它们不取那把锁，只在进程首次探测 native 时取一次另外的静态锁，
    /// 而那次探测必然早于任何渲染线程的存在。
    ///
    /// 写成规则而不是逐个点名，是因为点名的形式会烂：新增一个持锁方法而忘记
    /// 把它加进名单，名单就变成假的，而没有任何东西会发现。这不是假想——
    /// 改成规则之前的名单只列了三个方法，而实现里持那把锁的是五个。
    /// 规则不会因新增方法而失效。
    /// </summary>
    event Action<AudioFrame>? FramePlayed;

    /// <summary>
    /// 下发跨机对齐参数。
    ///
    /// 必须在 <see cref="Start"/> 之前至少调用一次：48kHz 端点的内环在起播那一刻按
    /// enabled 决定建不建重采样器，起播时若对齐是关的，该端点此后就没有执行器可用，
    /// 而那时声音照出、判据照绿，只是永远对不齐。
    ///
    /// 播放中调用是允许的，用于更新 offsetTicks 与 manualOffsetTicks 这两个会随对时结果
    /// 变化的量；但把 enabled 由假改真不会给 48kHz 端点追补内环。
    /// </summary>
    /// <param name="enabled">用户的对齐设置。它只表示用户开没开，不兼作 offset 的可用性。</param>
    /// <param name="dTicks">发送端声明的播放延迟预算，100 纳秒计次。</param>
    /// <param name="offsetTicks">本机单调时钟减发送端时钟；0 表示 offset 此刻不可用。</param>
    /// <param name="manualOffsetTicks">用户为本设备手调的偏移。</param>
    void SetAlignment(bool enabled, long dTicks, long offsetTicks, long manualOffsetTicks);

    void Start(int targetBufferMs);

    void Stop();

    /// <summary>送一块 s16le 交错 PCM 进播放缓冲。未启动时静默忽略。</summary>
    /// <param name="senderTicks">
    /// 本帧在发送端时间轴上的起始时刻，100ns。0 表示本帧没有时刻——此时播放侧不建
    /// 时间轴、不按空档补静音，逐字走原路径。对齐模式关闭时就该传 0。
    /// </param>
    void Push(byte[] pcm, long senderTicks);

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
