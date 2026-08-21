using System.Diagnostics;

namespace MediaIsland.Services.Audio;

/// <summary>
/// 单调时钟，刻度为 100ns。
///
/// 归一到 100ns 而非直接用 <see cref="Stopwatch.GetTimestamp"/> 的原始刻度：
/// <see cref="Stopwatch.Frequency"/> 不保证等于 10^7，而 WASAPI 的 pu64QPCPosition
/// 与协议里的对时字段都以 100ns 为单位。不归一就是把一个平台相关的刻度当成 100ns 用，
/// 错的是一个常数因子——而常数因子恰好不容易被「误差看起来稳定」暴露。
///
/// 收成一处的理由是它此前有三份：播放渲染器、音频广播、音频接收各自写了一遍同一个
/// 常量与同一个表达式。三份各自为真的换算就是漂移的起点，而这个换算一旦有一份写错，
/// 症状是时间轴整体缩放，不是崩溃。
///
/// 墙钟不在此列。墙钟会被 NTP 调整（slew 或 step），故凡是要跨分钟级持续维持的时间
/// 关系都不能用它；本类是那些场合的唯一时间源。
/// </summary>
internal static class MonotonicClock
{
    /// <summary>QPC 的名义单位：100ns，即 1 秒 = 10^7 个刻度。WASAPI 的 pu64QPCPosition 用的就是它。</summary>
    internal const long TicksPerSecond100Ns = 10_000_000;

    /// <summary>1 毫秒 = 10^4 个 100ns 刻度。</summary>
    internal const long TicksPerMs100Ns = 10_000;

    /// <summary>
    /// 当前单调时刻，单位 100ns。
    ///
    /// 与 <see cref="System.TimeSpan.Ticks"/> 同刻度，故两者之间无需换算代码。
    ///
    /// 零点不是日历时刻，只可用于求差；但它是系统级而非进程级的。Windows 上
    /// <see cref="Stopwatch.GetTimestamp"/> 就是原始 QPC，零点为系统启动，
    /// 故同一台机器上跨进程、跨托管与 native 的读数可以直接相减——音频广播把
    /// 本地时刻减去 native 填的 pu64QPCPosition，成立的正是这一条。
    /// 跨机器则零点不同，只能靠对时建立映射。
    /// </summary>
    internal static long Now100Ns() =>
        (long)(Stopwatch.GetTimestamp() * (double)TicksPerSecond100Ns / Stopwatch.Frequency);
}
