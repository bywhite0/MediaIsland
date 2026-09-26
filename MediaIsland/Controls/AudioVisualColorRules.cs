using Avalonia.Media;
using MediaIsland.Components;

namespace MediaIsland.Controls;

/// <summary>
/// 组件前景色的两步：先按颜色来源选出基色，再决定要不要按音色混色。
/// 抽成纯函数是因为回落写错不会报错——封面缺失时组件变白、主题资源缺失时变透明——
/// 只会在岛上看出来，而岛上的样子没有自动化测试能看到。
/// </summary>
public static class AudioVisualColorRules
{
    /// <summary>
    /// 封面取不到颜色时回落主题色，与「正在播放」进度条的封面主题色同一取舍；
    /// 主题资源也取不到（宿主主题缺这个键）时回落白色，保证总有一个可见的颜色。
    /// </summary>
    public static Color ResolveBase(AudioVisualColorSource source, Color? cover, Color? accent) => source switch
    {
        AudioVisualColorSource.White => Colors.White,
        AudioVisualColorSource.Cover => cover ?? accent ?? Colors.White,
        _ => accent ?? Colors.White
    };

    public static Color Foreground(Color baseColor, bool useTimbre, double centroid) =>
        useTimbre ? TimbreColor.Mix(baseColor, centroid) : baseColor;
}
