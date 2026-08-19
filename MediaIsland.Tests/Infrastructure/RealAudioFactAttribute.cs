using Xunit;

namespace MediaIsland.Tests.Infrastructure;

/// <summary>
/// 需要真实音频端点的测试。默认跳过，设 MEDIAISLAND_REAL_AUDIO=1 后启用。
///
/// xunit 2.9.2 没有运行时跳过（那是 v3 的 Assert.Skip），故走 attribute 构造期
/// 设 Skip 这条路。xunit 在发现阶段实例化 attribute 并读该属性，
/// 所以环境变量必须在测试进程启动前就位——对 dotnet test 成立。
///
/// 为什么不沿用硬编码 Skip：不是风格取舍。第 4 期为播放路径写了 14 条极详细的
/// 手工清单，执行次数为零；第 2 期写的两个真机检查至今仍带着硬编码 Skip。
/// 改代码才能跑的东西，成本足够高到实际等于永不跑。
/// </summary>
public sealed class RealAudioFactAttribute : FactAttribute
{
    public const string Variable = "MEDIAISLAND_REAL_AUDIO";

    public RealAudioFactAttribute()
    {
        Skip = ExternalConditionGate.SkipReasonFor(
            Variable,
            Environment.GetEnvironmentVariable(Variable),
            "真实音频端点，且默认输出未被静音");
    }
}
