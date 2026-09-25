namespace MediaIsland.Helpers;

/// <summary>
/// 「正在播放」系列组件在没有可显示媒体时的状态文本。
/// </summary>
internal static class NowPlayingStatusText
{
    public const string Idle = "未在播放";

    /// <summary>
    /// 仅在因没有可显示媒体（无会话、播放源已禁用、获取失败）而隐藏内容时显示；
    /// 「暂停时隐藏」是用户主动要求的隐藏，不补状态文本。
    /// </summary>
    public static bool IsVisible(bool isIdle, bool isShowStatusText) => isIdle && isShowStatusText;
}
