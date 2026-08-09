namespace MediaIsland.Services.Audio;

/// <summary>
/// 同步帧入口。
///
/// 与 <see cref="IAudioFrameSink"/> 的区别不是风格而是硬约束：协议解码路径上，
/// PCM 是从入站缓冲切出来的 <c>ReadOnlySpan&lt;byte&gt;</c>，而 ref struct 不允许
/// 出现在 async 方法体内（CS8175/CS9202）。解码、校验、拷贝必须在同一个同步作用域里做完，
/// 那里没有 await 可用，异步汇的契约在此处根本用不上。
///
/// 反过来，会真正阻塞的实现（网络广播、落盘）仍该用 <see cref="IAudioFrameSink"/>——
/// 让那种实现假装同步，只会把阻塞藏进调用方的线程里。
/// </summary>
public interface IAudioFrameSubmitter
{
    /// <summary>提交一帧。实现须几微秒内返回：调用方可能是音频线程或网络收循环。</summary>
    void Submit(AudioFrame frame);
}
