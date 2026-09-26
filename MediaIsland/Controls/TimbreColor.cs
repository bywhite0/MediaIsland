using Avalonia.Media;

namespace MediaIsland.Controls;

/// <summary>
/// 音色变色：质心 0.5 为基色，越亮越向暖黄、越沉越向深紫。两端色固定而不跟主题走——
/// 它们表达的是「亮/暗」这一物理含义，换主题不该让「亮」变成另一种颜色。
/// </summary>
public static class TimbreColor
{
    private static readonly Color Dark = Color.FromRgb(120, 70, 170);
    private static readonly Color Bright = Color.FromRgb(245, 190, 90);

    public static Color Mix(Color baseColor, double centroid)
    {
        var c = double.IsNaN(centroid) ? 0.5 : Math.Clamp(centroid, 0, 1);
        // 透明度恒取基色：两端色只管色相，不该让半透明基色在暗半段突然变成不透明。
        return c < 0.5
            ? Lerp(baseColor.A, Dark, baseColor, c * 2)
            : Lerp(baseColor.A, baseColor, Bright, (c - 0.5) * 2);
    }

    private static Color Lerp(byte alpha, Color from, Color to, double t) => Color.FromArgb(
        alpha,
        (byte)Math.Round(from.R + (to.R - from.R) * t),
        (byte)Math.Round(from.G + (to.G - from.G) * t),
        (byte)Math.Round(from.B + (to.B - from.B) * t));
}
