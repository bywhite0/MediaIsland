using Avalonia;
using Avalonia.Media;
using MediaIsland.Components;

namespace MediaIsland.Controls;

public sealed class AudioLevelPresenter : AudioVisualPresenterBase
{
    public static readonly StyledProperty<LevelStyle> StyleProperty =
        AvaloniaProperty.Register<AudioLevelPresenter, LevelStyle>(nameof(Style));

    public static readonly StyledProperty<double> RmsProperty =
        AvaloniaProperty.Register<AudioLevelPresenter, double>(nameof(Rms));

    public static readonly StyledProperty<double> PeakProperty =
        AvaloniaProperty.Register<AudioLevelPresenter, double>(nameof(Peak));

    public static readonly StyledProperty<double> LeftProperty =
        AvaloniaProperty.Register<AudioLevelPresenter, double>(nameof(Left));

    public static readonly StyledProperty<double> RightProperty =
        AvaloniaProperty.Register<AudioLevelPresenter, double>(nameof(Right));

    static AudioLevelPresenter() => AffectsRender<AudioLevelPresenter>(
        StyleProperty, RmsProperty, PeakProperty, LeftProperty, RightProperty);

    public LevelStyle Style { get => GetValue(StyleProperty); set => SetValue(StyleProperty, value); }
    public double Rms { get => GetValue(RmsProperty); set => SetValue(RmsProperty, value); }
    public double Peak { get => GetValue(PeakProperty); set => SetValue(PeakProperty, value); }
    public double Left { get => GetValue(LeftProperty); set => SetValue(LeftProperty, value); }
    public double Right { get => GetValue(RightProperty); set => SetValue(RightProperty, value); }

    public override void Render(DrawingContext context)
    {
        var (w, h) = (Bounds.Width, Bounds.Height);
        if (w <= 0 || h <= 0) return;
        var brush = Brush;
        var track = WithOpacity(brush, RestOpacity);

        if (Style == LevelStyle.Meter)
        {
            var (bg, fill, tick) = LevelGeometry.Meter(Rms, Peak, w, h);
            DrawCapsule(context, track, bg);
            // 底轨是满高胶囊（圆角取短边一半），填充与刻线按底轨轮廓裁：否则方角填充和贴右端的刻线
            // 任何电平下都伸出圆端。
            using (context.PushClip(new RoundedRect(bg, Math.Min(bg.Width, bg.Height) / 2)))
            {
                if (fill.Width > 0) context.FillRectangle(brush, fill);
                if (tick.Width > 0) context.FillRectangle(brush, tick);
            }

            return;
        }

        var (rail, centerTick, dot, radius) = LevelGeometry.Pan(Left, Right, w, h);
        DrawCapsule(context, track, rail);
        context.FillRectangle(WithOpacity(brush, 0.3), centerTick);
        DrawCircle(context, brush, dot, radius);
    }
}
