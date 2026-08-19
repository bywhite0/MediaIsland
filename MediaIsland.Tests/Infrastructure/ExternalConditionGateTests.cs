using Xunit;

namespace MediaIsland.Tests.Infrastructure;

/// <summary>
/// 门控判定本身的判据。
///
/// 为什么这几条不是多余的：门控若判反了，整套真机测试会静默不跑，
/// 而「skipped 数」在没人核对的情况下与「本来就该跳过」无从区分。
/// 这是本仓唯一一处「测试基础设施自身」的测试，理由是它一旦失效，
/// 失效的表现形式恰好是「一切正常」。
/// </summary>
public class ExternalConditionGateTests
{
    [Fact]
    public void EnabledValue_YieldsNoSkipReason()
    {
        Assert.Null(ExternalConditionGate.SkipReasonFor(
            "MEDIAISLAND_X", ExternalConditionGate.EnabledValue, "真实音频端点"));
    }

    [Fact]
    public void AbsentVariable_YieldsSkipReason()
    {
        var reason = ExternalConditionGate.SkipReasonFor("MEDIAISLAND_X", null, "真实音频端点");

        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void EmptyVariable_YieldsSkipReason()
    {
        // 空串与未设置必须同判。「设了但为空」是常见形态，
        // 若只判 null，空串会被当成已启用而让整套真机测试在无设备的机器上真跑。
        Assert.NotNull(ExternalConditionGate.SkipReasonFor("MEDIAISLAND_X", "", "真实音频端点"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("true")]
    [InlineData("yes")]
    [InlineData("1 ")]
    public void NonExactValues_YieldSkipReason(string value)
    {
        // 只认恰好的 1。宽松匹配（true / yes / 去空白后的 1）会让
        // 「我以为开了其实没开」与「我以为没开其实开了」两种误解同时可能，
        // 而一个精确值把这两种都消掉。
        Assert.NotNull(ExternalConditionGate.SkipReasonFor("MEDIAISLAND_X", value, "真实音频端点"));
    }

    [Fact]
    public void SkipReason_NamesTheVariableAndTheEnablingValue()
    {
        // 文案要能被直接照做。只说「需要真实设备」而不说怎么开，
        // 与硬编码 Skip 的成本没有区别——而降低那个成本正是本机制的全部目的。
        var reason = ExternalConditionGate.SkipReasonFor("MEDIAISLAND_X", null, "真实音频端点");

        Assert.Contains("MEDIAISLAND_X", reason);
        Assert.Contains(ExternalConditionGate.EnabledValue, reason);
        Assert.Contains("真实音频端点", reason);
    }
}
