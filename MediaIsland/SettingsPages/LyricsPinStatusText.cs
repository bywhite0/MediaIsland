namespace MediaIsland.SettingsPages;

/// <summary>
/// 固定歌词的状态文案。
/// </summary>
/// <remarks>
/// 用户多半是在某个播放器下点的「固定」，未必意识到固定是跨播放器全局的。
/// 因此固定存在但未生效时必须说明原因，否则用户会认为固定功能坏了。
/// </remarks>
internal static class LyricsPinStatusText
{
    public static string Describe(
        bool hasPin,
        string? pinSource,
        DateTimeOffset? pinnedAtUtc,
        bool isSuppressedBySPlayerNext,
        bool isSearchDisabled)
    {
        if (!hasPin)
        {
            return "当前曲目未固定歌词。双击候选可应用，点击「固定当前歌词」后将长期保留。";
        }

        var source = string.IsNullOrWhiteSpace(pinSource) ? "未知来源" : pinSource;
        var date = pinnedAtUtc?.ToLocalTime().ToString("yyyy-MM-dd") ?? "未知日期";
        var summary = $"已固定（来源：{source} · {date}）。固定对所有播放器生效。";

        // 门禁在优先级链上比 SPlayer-Next 直连更靠后但覆盖面更广，两者同时成立时先报门禁。
        if (isSearchDisabled)
        {
            return summary + " 当前未生效：该播放源已关闭歌词搜索，本机不解析歌词。";
        }

        if (isSuppressedBySPlayerNext)
        {
            return summary + " 当前未生效：SPlayer-Next 正提供其自身的歌词。";
        }

        return summary;
    }
}
