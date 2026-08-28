using System.Globalization;

namespace MediaIsland.Services.MediaLink;

/// <summary>
/// 判定快照到设置页状态行三段文案的组装。抽成纯函数是因为页面在测试里构造不了
/// （本仓无 Avalonia.Headless）——判据打在这层，页面只剩取快照、拼行与绑定。
/// 归因用快照里与日志同源的那句 Reason，页面不重算归因。
/// </summary>
internal static class MediaLinkAlignmentStatusLine
{
    /// <summary>没有判定（服务未起、读取失败）时的占位。约定值：显示它页面不炸。</summary>
    internal const string Unavailable = "暂无";

    /// <summary>
    /// 三段：归因、误差、目标深度。
    ///
    /// 归因两处改口。开关关着时四态结论只是「假如开着」的推演，原句照显会与用户
    /// 刚拨下去的开关自相矛盾；出声前的 Aligned 是未经设备事实核验的乐观结论，
    /// 与协调方日志同一纪律——不宣布，改说等待出声。其余状态的 Reason 原样透出，
    /// 页面与日志各说各话的口子不重新打开。
    ///
    /// 误差只在对齐生效中给数字：未对齐或未出声时 stats 里是零或上一段的残值，
    /// 显出来会像还在对齐。目标深度同理只看出声与否；区间是常量，恒可显示——
    /// 校准手动偏移的用户要靠它知道当前值离边界多远。
    /// </summary>
    internal static (string Attribution, string Error, string Depth) Compose(
        MediaLinkAlignmentSnapshot? snapshot)
    {
        if (snapshot is not { } s)
        {
            return (Unavailable, $"误差 {Unavailable}", $"目标深度 {Unavailable}");
        }

        var attribution = !s.Enabled
            ? "对齐未启用"
            : s is { State: MediaLinkAlignmentState.Aligned, HasStarted: false }
                ? "等待出声后核验对齐"
                : s.Reason;
        var error = s is { Enabled: true, HasStarted: true, State: MediaLinkAlignmentState.Aligned }
            // 小数点定成不变文化：状态行是诊断读数，随系统文化换成逗号会让
            // 「照着状态行抄数报障」两端对不上。
            ? string.Create(CultureInfo.InvariantCulture, $"误差 {s.PlayTimeErrorUs / 1_000.0:F1}ms")
            : $"误差 {Unavailable}";
        var depth = s.HasStarted
            ? $"目标深度 {s.TargetMsCurrent}ms（区间 {s.MinTargetMs}–{s.MaxTargetMs}ms）"
            : $"目标深度 {Unavailable}（区间 {s.MinTargetMs}–{s.MaxTargetMs}ms）";
        return (attribution, error, depth);
    }

    /// <summary>三段拼成一行，页面直接绑定。没有快照时整行只有一个「暂无」，不拼三个。</summary>
    internal static string ComposeLine(MediaLinkAlignmentSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return Unavailable;
        }

        var (attribution, error, depth) = Compose(snapshot);
        return $"{attribution} · {error} · {depth}";
    }
}
