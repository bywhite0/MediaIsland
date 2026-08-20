namespace MediaIsland.Services.Lyrics;

/// <summary>
/// 歌词呈现位置的合成。
/// </summary>
/// <remarks>
/// 抽成纯函数不是为了复用——只有歌词组件一个调用方——而是为了可测。它内联在组件里时，
/// 唯一能验的办法是构造控件，而本项目没有引 Avalonia.Headless、也没有一处测试构造过控件，
/// 那等于这段算术没有判据。而它出错的表现是「歌词整体偏了一点」，
/// 恰好是最容易被当成主观感受而不是缺陷的一种。
/// </remarks>
internal static class LyricsPresentationPosition
{
    /// <summary>
    /// 合成当前应当呈现的歌词位置。
    /// </summary>
    /// <param name="clock">媒体时钟给出的位置。跨机场景下它对齐的是对方的实时位置。</param>
    /// <param name="sourceOffset">按歌词来源配置的偏移，用于修正各家提供者的固有时间偏差。</param>
    /// <param name="outputLatency">
    /// 本机音频输出延迟。**减而不是加**：时钟对齐的是对方的实时位置，而本机喇叭此刻发出的
    /// 是抖动缓冲里攒了一段时间的那批帧，歌词必须后移同样多才对得上听觉。
    /// 本机不出声时它为零，那时对方的实时位置本身就是对的（听觉参照是对方的喇叭或者没有）。
    /// </param>
    /// <remarks>
    /// 结果钳到非负。歌曲刚起播、位置还小于缓冲深度时，减出来是负值，
    /// 而负位置会让选行落到第一行之前，表现为开头几百毫秒不显示歌词——
    /// 那是补偿本身引入的新缺陷，必须在这里挡掉。
    /// </remarks>
    internal static TimeSpan Compose(TimeSpan clock, TimeSpan sourceOffset, TimeSpan outputLatency)
    {
        var position = clock + sourceOffset - outputLatency;
        return position < TimeSpan.Zero ? TimeSpan.Zero : position;
    }
}
