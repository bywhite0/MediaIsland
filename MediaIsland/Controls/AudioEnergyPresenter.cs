using Avalonia;
using Avalonia.Media;
using MediaIsland.Components;

namespace MediaIsland.Controls;

/// <summary>音频能量的六种画法。几何全部来自 <see cref="EnergyGeometry"/>。</summary>
public sealed class AudioEnergyPresenter : AudioVisualPresenterBase
{
    public static readonly StyledProperty<EnergyStyle> StyleProperty =
        AvaloniaProperty.Register<AudioEnergyPresenter, EnergyStyle>(nameof(Style));

    public static readonly StyledProperty<double> EnergyProperty =
        AvaloniaProperty.Register<AudioEnergyPresenter, double>(nameof(Energy));

    public static readonly StyledProperty<double> PhaseSecondsProperty =
        AvaloniaProperty.Register<AudioEnergyPresenter, double>(nameof(PhaseSeconds));

    public static readonly StyledProperty<IReadOnlyList<RippleRing>> RingsProperty =
        AvaloniaProperty.Register<AudioEnergyPresenter, IReadOnlyList<RippleRing>>(nameof(Rings), []);

    public static readonly StyledProperty<IReadOnlyList<float>> HistoryProperty =
        AvaloniaProperty.Register<AudioEnergyPresenter, IReadOnlyList<float>>(nameof(History), []);

    public static readonly StyledProperty<int> BeatIndexProperty =
        AvaloniaProperty.Register<AudioEnergyPresenter, int>(nameof(BeatIndex), -1);

    public static readonly StyledProperty<double> BeatGlowProperty =
        AvaloniaProperty.Register<AudioEnergyPresenter, double>(nameof(BeatGlow));

    static AudioEnergyPresenter() => AffectsRender<AudioEnergyPresenter>(
        StyleProperty, EnergyProperty, PhaseSecondsProperty, RingsProperty, HistoryProperty, BeatIndexProperty, BeatGlowProperty);

    public EnergyStyle Style { get => GetValue(StyleProperty); set => SetValue(StyleProperty, value); }
    public double Energy { get => GetValue(EnergyProperty); set => SetValue(EnergyProperty, value); }
    public double PhaseSeconds { get => GetValue(PhaseSecondsProperty); set => SetValue(PhaseSecondsProperty, value); }
    public IReadOnlyList<RippleRing> Rings { get => GetValue(RingsProperty); set => SetValue(RingsProperty, value); }
    public IReadOnlyList<float> History { get => GetValue(HistoryProperty); set => SetValue(HistoryProperty, value); }
    public int BeatIndex { get => GetValue(BeatIndexProperty); set => SetValue(BeatIndexProperty, value); }
    public double BeatGlow { get => GetValue(BeatGlowProperty); set => SetValue(BeatGlowProperty, value); }

    public override void Render(DrawingContext context)
    {
        var (w, h) = (Bounds.Width, Bounds.Height);
        if (w <= 0 || h <= 0) return;
        var brush = Brush;
        var color = ColorOf(brush);

        switch (Style)
        {
            case EnergyStyle.Orb:
            {
                var (center, r, glow) = EnergyGeometry.Orb(Energy, w, h);
                DrawGlow(context, color, center, r * 0.6, glow, 0.1 + 0.55 * Energy);
                DrawCircle(context, brush, center, r);
                break;
            }
            case EnergyStyle.Ripple:
            {
                var (center, core, rings) = EnergyGeometry.Ripple(Energy, Rings, w, h);
                foreach (var (radius, opacity) in rings)
                {
                    context.DrawEllipse(null, new Pen(WithOpacity(brush, opacity), 1.5), center, radius, radius);
                }

                DrawCircle(context, brush, center, core);
                break;
            }
            case EnergyStyle.GlowLine:
            {
                var (line, opacity) = EnergyGeometry.GlowLine(Energy, w, h);
                var fade = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 0),
                        new GradientStop(Color.FromArgb((byte)(255 * opacity), color.R, color.G, color.B), 0.5),
                        new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1)
                    }
                };
                DrawCapsule(context, fade, line);
                break;
            }
            case EnergyStyle.Dots:
            {
                var (centers, r) = EnergyGeometry.Dots(Energy, PhaseSeconds, w, h);
                foreach (var c in centers) DrawCircle(context, brush, c, r);
                break;
            }
            case EnergyStyle.History:
            {
                var points = EnergyGeometry.History(History, EnergyGeometry.HistoryCapacity, w, h);
                var geometry = new StreamGeometry();
                using (var sink = geometry.Open())
                {
                    sink.BeginFigure(points[0], false);
                    for (var k = 1; k < points.Length; k++) sink.LineTo(points[k]);
                    sink.EndFigure(false);
                }

                // 左端渐隐：越旧的越淡，视线自然落在右缘的「此刻」。
                var fade = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 0),
                        new GradientStop(Color.FromArgb(153, color.R, color.G, color.B), 0.3),
                        new GradientStop(color, 1)
                    }
                };
                context.DrawGeometry(null, new Pen(fade, 1.5, lineJoin: PenLineJoin.Round), geometry);
                break;
            }
            case EnergyStyle.Beat:
            {
                var (centers, r) = EnergyGeometry.Beat(w, h);
                for (var k = 0; k < centers.Length; k++)
                {
                    var on = k == BeatIndex;
                    if (on) DrawGlow(context, color, centers[k], r * 0.4, r * 1.8 * 2, 0.6 * BeatGlow);
                    DrawCircle(context, on ? WithOpacity(brush, 0.45 + 0.55 * BeatGlow) : WithOpacity(brush, TrackOpacity),
                        centers[k], r + (on ? BeatGlow * 1.5 : 0));
                }

                break;
            }
        }
    }

    private static void DrawGlow(DrawingContext context, Color color, Point center, double inner, double outer, double alpha)
    {
        if (outer <= 0 || alpha <= 0) return;
        var glow = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromArgb((byte)(255 * Math.Clamp(alpha, 0, 1)), color.R, color.G, color.B), inner / outer),
                new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1)
            }
        };
        context.DrawEllipse(glow, null, center, outer, outer);
    }
}
