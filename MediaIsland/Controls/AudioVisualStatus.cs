namespace MediaIsland.Controls;

public enum AudioVisualStatus
{
    /// <summary>有数据在流动。</summary>
    Active,

    /// <summary>有数据源但没有声音：画静止形态并停表。</summary>
    Idle,

    /// <summary>本机采集不可用且没有上游：静止形态变暗。</summary>
    Unavailable
}

/// <summary>
/// 状态判定与提示文案。抽成纯函数：判错的两种后果——该暗不暗、不该暗却暗——都不报错，
/// 只会让用户分不清「没声音」和「组件坏了」，而那正是这次重做要解决的原问题。
/// </summary>
public static class AudioVisualStatusRules
{
    public const double UnavailableOpacity = 0.35;

    public const string Explanation = "显示本机正在播放的声音，或 Link-Like MediaLink 接收到的上游音频。无需开启共享。";

    public static AudioVisualStatus Resolve(bool localSourceAvailable, bool consumingUpstream, bool hasRecentData)
    {
        if (hasRecentData)
        {
            return AudioVisualStatus.Active;
        }

        return localSourceAvailable || consumingUpstream ? AudioVisualStatus.Idle : AudioVisualStatus.Unavailable;
    }

    public static double OpacityFor(AudioVisualStatus status) =>
        status == AudioVisualStatus.Unavailable ? UnavailableOpacity : 1;

    public static (string Text, bool IsWarning) Describe(
        bool localSourceAvailable, string? failureReason, bool consumingUpstream, bool silent)
    {
        if (consumingUpstream)
        {
            return (Suffix("数据来源：Link-Like MediaLink 上游", silent), false);
        }

        if (localSourceAvailable)
        {
            return (Suffix("数据来源：本机采集", silent), false);
        }

        return ($"无法采集本机音频：{failureReason ?? "原因未知"}。上游有音频时仍可显示。", true);
    }

    private static string Suffix(string text, bool silent) => silent ? text + "（当前无声音）" : text;
}
