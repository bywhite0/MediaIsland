using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MediaIsland.Controls;

/// <summary>
/// 四个音频 presenter 的共同部分：前景笔刷与两种基本图元。几何都在各自的纯静态类里，
/// presenter 只负责取笔刷、分派样式与调用绘制 API——那部分才是无 UI 环境测不了的。
/// </summary>
public abstract class AudioVisualPresenterBase : Control
{
    /// <summary>
    /// 继承属性：组件要主题色时 ClearValue 让继承链送下来，要固定色或音色色时显式赋值。
    /// </summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<AudioVisualPresenterBase, IBrush?>(nameof(Foreground), inherits: true);

    static AudioVisualPresenterBase() => AffectsRender<AudioVisualPresenterBase>(ForegroundProperty);

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected IBrush Brush => Foreground ?? Brushes.White;

    /// <summary>全圆角矩形。圆角取短边一半：低幅度时是细条而不是橄榄形。</summary>
    protected static void DrawCapsule(DrawingContext context, IBrush brush, Rect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;
        var radius = Math.Min(rect.Width, rect.Height) / 2;
        context.DrawRectangle(brush, null, rect, radius, radius);
    }

    protected static void DrawCircle(DrawingContext context, IBrush brush, Point center, double radius)
    {
        if (radius <= 0) return;
        context.DrawEllipse(brush, null, center, radius, radius);
    }

    /// <summary>纯色笔刷换透明度；非纯色原样返回——渐变笔刷不在本组件的取色路径上。</summary>
    protected static IBrush WithOpacity(IBrush brush, double opacity) =>
        brush is ISolidColorBrush solid
            ? new SolidColorBrush(solid.Color, Math.Clamp(opacity, 0, 1))
            : brush;

    protected static Color ColorOf(IBrush brush) =>
        brush is ISolidColorBrush solid ? solid.Color : Colors.White;

    /// <summary>底轨/暗格的统一不透明度。</summary>
    protected const double TrackOpacity = 0.18;
}
