namespace MediaIsland.Tests.Infrastructure;

/// <summary>
/// 需要外部条件的测试的门控判定。
///
/// 抽成纯函数而不是写在 attribute 构造里，是为了让它可测：attribute 必须读进程
/// 环境变量，而在测试里改进程环境变量会与并行跑的其他测试竞态。
/// 纯函数把「读」与「判」分开，判的那一半不碰进程状态。
///
/// 三态而非两态是本类型存在的理由。一条依赖外部条件的测试，在条件不满足时既不是
/// 通过也不是失败，而是未判定。xunit 只有绿、红、跳过三种结果，未判定唯一诚实的
/// 表达是跳过。此前这类测试有两种写法，都在说谎：硬编码 Skip 让它永远不跑，
/// 探测失败即 return 让它报绿。后者更坏——它使「全量 N passed」这句话里含有
/// 一条假的，且从计数上看不出来。
/// </summary>
internal static class ExternalConditionGate
{
    /// <summary>启用值。只认恰好这个串。</summary>
    public const string EnabledValue = "1";

    /// <summary>
    /// 判是否该跳过。返回 null 表示条件已满足、该真跑；
    /// 返回非空表示跳过的理由，该理由会显示在测试结果里。
    /// </summary>
    /// <param name="variable">环境变量名，写进文案供读者照做。</param>
    /// <param name="value">该变量的当前值，由调用方读取后传入。</param>
    /// <param name="condition">需要什么条件，写进文案。</param>
    public static string? SkipReasonFor(string variable, string? value, string condition)
    {
        if (value == EnabledValue)
        {
            return null;
        }

        return $"需{condition}。设 {variable}={EnabledValue} 后重跑，步骤见 AGENTS.md";
    }
}
