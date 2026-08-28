using MediaIsland.Services.MediaLink;
using Xunit;

namespace MediaIsland.Tests.MediaLink;

/// <summary>
/// 状态行三段的组装。页面在测试里构造不了（无 Avalonia.Headless），页面层只剩
/// 取快照与绑定——诊断面用户可见那一半的判据全部落在这层：归因何时照抄 Reason、
/// 何时改口，数字何时给、何时说「暂无」。
/// </summary>
public class MediaLinkAlignmentStatusLineTests
{
    private static MediaLinkAlignmentSnapshot Snapshot(
        bool enabled = true,
        bool hasStarted = true,
        MediaLinkAlignmentState state = MediaLinkAlignmentState.Aligned,
        string reason = "正在对齐播放",
        long errorUs = -7_200,
        int targetMs = 180,
        int minMs = 50,
        int maxMs = 1_000) =>
        new(enabled, hasStarted, state, reason, errorUs, targetMs, minMs, maxMs);

    [Fact]
    public void NoSnapshot_EverySegmentSaysUnavailable_AndTheLineIsOneWord()
    {
        // 服务未起、协调方未建、读取失败全走这条路：显示「暂无」而不是炸页面。
        // 整行只有一个「暂无」——三段各拼一个「暂无」是把占位符当成了三个读数。
        Assert.Equal(("暂无", "误差 暂无", "目标深度 暂无"), MediaLinkAlignmentStatusLine.Compose(null));
        Assert.Equal("暂无", MediaLinkAlignmentStatusLine.ComposeLine(null));
    }

    [Fact]
    public void AlignedAndSounding_ShowsReasonErrorNumberAndDepth()
    {
        // 主形态：归因照抄快照里与日志同源的 Reason，误差微秒换毫秒一位小数，
        // 深度带当前值与区间。用户校准手动偏移看的就是这行的负号与数量级。
        var (attribution, error, depth) = MediaLinkAlignmentStatusLine.Compose(Snapshot());

        Assert.Equal("正在对齐播放", attribution);
        Assert.Equal("误差 -7.2ms", error);
        Assert.Equal("目标深度 180ms（区间 50–1000ms）", depth);
        Assert.Equal(
            "正在对齐播放 · 误差 -7.2ms · 目标深度 180ms（区间 50–1000ms）",
            MediaLinkAlignmentStatusLine.ComposeLine(Snapshot()));

        // 正方向与四舍五入一并钉住：符号被吞或量级滑一档都在此红。
        Assert.Equal("误差 12.3ms", MediaLinkAlignmentStatusLine.Compose(Snapshot(errorUs: 12_340)).Error);
    }

    [Fact]
    public void SwitchedOff_RestatesInsteadOfEchoingTheHypothetical()
    {
        // 开关关着时四态结论是「假如开着」的推演，照显「正在对齐播放」会与用户
        // 刚拨下去的开关自相矛盾；误差数字同理不给。
        var (attribution, error, _) = MediaLinkAlignmentStatusLine.Compose(Snapshot(enabled: false));

        Assert.Equal("对齐未启用", attribution);
        Assert.Equal("误差 暂无", error);
    }

    [Fact]
    public void AlignedBeforeSound_IsNotAnnounced()
    {
        // 出声前的 Aligned 未经设备事实核验，与协调方日志同一纪律：不宣布。
        // 误差此时恒为零，给数字会像已经对得很准。
        var (attribution, error, _) = MediaLinkAlignmentStatusLine.Compose(
            Snapshot(hasStarted: false, errorUs: 0));

        Assert.Equal("等待出声后核验对齐", attribution);
        Assert.Equal("误差 暂无", error);
    }

    [Fact]
    public void NotAligned_ShowsTheReasonVerbatim_WithoutAnErrorNumber()
    {
        // 四态文案是单一真相源：不对齐的归因原样透出，这层不改写不缩写。
        // stats 里的误差是上一段对齐的残值，显出来会像还在对齐。
        var (attribution, error, _) = MediaLinkAlignmentStatusLine.Compose(Snapshot(
            state: MediaLinkAlignmentState.NoClockOffset,
            reason: "尚未与服务端对上时钟，暂按不对齐播放",
            errorUs: 12_000));

        Assert.Equal("尚未与服务端对上时钟，暂按不对齐播放", attribution);
        Assert.Equal("误差 暂无", error);
    }

    [Theory]
    [InlineData((int)MediaLinkAlignmentState.NoCapability, "服务端不支持跨机对时，已按不对齐播放")]
    [InlineData((int)MediaLinkAlignmentState.NoBudgetDeclared, "服务端未声明播放延迟预算，已按不对齐播放")]
    [InlineData((int)MediaLinkAlignmentState.BudgetTooSmall, "播放延迟预算 12000ms 超出上界 10000ms，本机无从执行，已按不对齐播放")]
    public void OtherPassThroughStates_EchoTheReasonVerbatim(int stateRaw, string reason)
    {
        // 归因臂对未特判态原样透出 s.Reason，而此前判据只过了 Aligned / NoClockOffset
        // 两态——透传臂改成常量串时其余三个透传态无声。五态逐一过一遍才闸得住。
        // 状态经 int 走线：枚举是 internal，公开测试方法的签名带不了它（xUnit v2
        // 只发现 public 方法）。
        var (attribution, _, _) = MediaLinkAlignmentStatusLine.Compose(
            Snapshot(state: (MediaLinkAlignmentState)stateRaw, reason: reason));

        Assert.Equal(reason, attribution);
    }

    [Fact]
    public void BeforeSound_TheDepthValueIsUnavailableButTheRangeShows()
    {
        // 未出声时深度读数是零，不冒充；区间是常量恒可显示——用户在开播前
        // 就能知道这台机器的目标深度会落在哪个范围里。
        var (_, _, depth) = MediaLinkAlignmentStatusLine.Compose(
            Snapshot(hasStarted: false, targetMs: 0));

        Assert.Equal("目标深度 暂无（区间 50–1000ms）", depth);
    }
}
