namespace MediaIsland.Models;

/// <summary>
/// 正在播放组件进度条颜色来源。
/// </summary>
public enum ProgressBarColorMode
{
    /// <summary>
    /// 使用 ClassIsland 主题强调色。
    /// </summary>
    ClassIslandTheme = 0,

    /// <summary>
    /// 从专辑封面提取主题色；不可用时回退到 ClassIsland 主题色。
    /// </summary>
    CoverTheme = 1
}
