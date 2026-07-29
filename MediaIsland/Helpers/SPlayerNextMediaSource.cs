namespace MediaIsland.Helpers;

/// <summary>
/// 识别 SPlayer-Next 的 SMTC 播放源标识。
/// </summary>
public static class SPlayerNextMediaSource
{
    public const string SourceAppId = "top.imsyy.splayer-next";

    public static bool Matches(string? sourceApp) =>
        !string.IsNullOrWhiteSpace(sourceApp) &&
        string.Equals(sourceApp, SourceAppId, StringComparison.OrdinalIgnoreCase);
}
