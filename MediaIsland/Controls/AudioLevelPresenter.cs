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
        var track = WithOpacity(brush, TrackOpacity);

        if (Style == LevelStyle.Meter)
        {
            var (bg, fill, tick) = LevelGeometry.Meter(Rms, Peak, w, h);
            DrawCapsule(context, track, bg);
            if (fill.Width > 0) context.DrawRectangle(brush, null, fill, Math.Min(1, fill.Height / 2), Math.Min(1, fill.Height / 2));
            if (tick.Width > 0) context.FillRectangle(brush, tick);
            return;
        }

        var (rail, centerTick, dot, radius) = LevelGeometry.Pan(Left, Right, w, h);
        DrawCapsule(context, track, rail);
        context.FillRectangle(WithOpacity(brush, 0.3), centerTick);
        DrawCircle(context, brush, dot, radius);
    }
}
