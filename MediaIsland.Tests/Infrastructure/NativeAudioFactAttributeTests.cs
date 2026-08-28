using Xunit;

namespace MediaIsland.Tests.Infrastructure;

/// <summary>
/// 原生库门控判定的判据。理由同 ExternalConditionGateTests：门控判反的表现
/// 恰好是「一切正常」——该跳的真跑（缺库环境死于异常）或该跑的全跳
/// （skipped 悄悄膨胀而没人核对每一条的理由）。
/// </summary>
public class NativeAudioFactAttributeTests
{
    [Fact]
    public void AnAvailableLibrary_YieldsNoSkipReason()
    {
        Assert.Null(NativeAudioFactAttribute.SkipReasonFor(isAvailable: true, failureReason: null));
    }

    [Fact]
    public void AMissingLibrary_SkipsAndNamesTheProbedCause()
    {
        // 缺库模拟形态：探测结果注入纯函数，不真挪 DLL——挪了会污染同进程
        // 其他测试（AudioRenderNative 的探测结果是静态缓存的）。
        var reason = NativeAudioFactAttribute.SkipReasonFor(
            isAvailable: false, failureReason: "找不到 MediaIsland.Audio 原生库");

        Assert.NotNull(reason);
        // 文案要能被直接照做：带上探测到的原因与「怎么补」。
        Assert.Contains("找不到 MediaIsland.Audio 原生库", reason);
        Assert.Contains("dotnet build", reason);
    }

    [Fact]
    public void AMissingLibraryWithoutAReason_StillSkips()
    {
        // FailureReason 理论上与 IsAvailable 同写，但门控不赌这个不变式：
        // 缺原因也得跳，绝不能因为文案拼不出来就放行真跑。
        Assert.NotNull(NativeAudioFactAttribute.SkipReasonFor(isAvailable: false, failureReason: null));
    }
}
