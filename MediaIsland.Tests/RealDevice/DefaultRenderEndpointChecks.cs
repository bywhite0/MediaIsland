using MediaIsland.Services.Audio.Playback;
using MediaIsland.Tests.Infrastructure;
using Xunit;

namespace MediaIsland.Tests.RealDevice;

/// <summary>
/// 默认渲染端点标识的真机验证。
///
/// COM 接口的 vtable 声明只有真调用能证明：槽位声明错了不会编译失败，调用会落到
/// 相邻的方法上，返回值还可能照样是 S_OK——那种错从单元测试层面完全不可见。
/// </summary>
[Collection(nameof(RealDeviceCollection))]
public class DefaultRenderEndpointChecks
{
    [RealAudioFact]
    public void TheDefaultRenderEndpoint_HasAStableWellFormedId()
    {
        var first = DefaultRenderEndpoint.TryGetId();
        var second = DefaultRenderEndpoint.TryGetId();

        Assert.False(string.IsNullOrWhiteSpace(first));

        // 同一时刻问两次要同一个答案：这个 ID 是 per-device 偏移的字典键，
        // 抖动的键等于每次读写落在不同的桶里。
        Assert.Equal(first, second);

        // 渲染端点 ID 的固定前缀（capture 是 {0.0.1.00000000}）。调错 vtable
        // 槽位拿回来的字符串不长这样——这条断言就是冲着那种错写的。
        Assert.StartsWith("{0.0.0.00000000}", first, StringComparison.Ordinal);
    }
}
