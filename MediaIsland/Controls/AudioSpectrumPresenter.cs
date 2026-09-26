using Avalonia;
using Avalonia.Media;
using MediaIsland.Components;

namespace MediaIsland.Controls;

/// <summary>音频频谱的六种画法。几何全部来自 <see cref="SpectrumGeometry"/>。</summary>
public sealed class AudioSpectrumPresenter : AudioVisualPresenterBase
{
    public static readonly StyledProperty<SpectrumStyle> StyleProperty =
        AvaloniaProperty.Register<AudioSpectrumPresenter, SpectrumStyle>(nameof(Style));

    public static readonly StyledProperty<IReadOnlyList<float>> BandsProperty =
        AvaloniaProperty.Register<AudioSpectrumPresenter, IReadOnlyList<float>>(nameof(Bands), []);

    public static readonly StyledProperty<IReadOnlyList<float>> PeaksProperty =
        AvaloniaProperty.Register<AudioSpectrumPresenter, IReadOnlyList<float>>(nameof(Peaks), []);

    public static readonly StyledProperty<IReadOnlyList<float[]>> HistoryProperty =
        AvaloniaProperty.Register<AudioSpectrumPresenter, IReadOnlyList<float[]>>(nameof(History), []);

    public static readonly StyledProperty<bool> MirroredProperty =
        AvaloniaProperty.Register<AudioSpectrumPresenter, bool>(nameof(Mirrored));

    public static readonly StyledProperty<double> BarGapProperty =
        AvaloniaProperty.Register<AudioSpectrumPresenter, double>(nameof(BarGap), 2);

    static AudioSpectrumPresenter() => AffectsRender<AudioSpectrumPresenter>(
        StyleProperty, BandsProperty, PeaksProperty, HistoryProperty, MirroredProperty, BarGapProperty);

    public SpectrumStyle Style { get => GetValue(StyleProperty); set => SetValue(StyleProperty, value); }

    /// <summary>当前样式的主数据：B/C/D 为频段，N3 为低中高三值，N5 为十二半音。</summary>
    public IReadOnlyList<float> Bands { get => GetValue(BandsProperty); set => SetValue(BandsProperty, value); }

    public IReadOnlyList<float> Peaks { get => GetValue(PeaksProperty); set => SetValue(PeaksProperty, value); }

    public IReadOnlyList<float[]> History { get => GetValue(HistoryProperty); set => SetValue(HistoryProperty, value); }

    public bool Mirrored { get => GetValue(MirroredProperty); set => SetValue(MirroredProperty, value); }

    public double BarGap { get => GetValue(BarGapProperty); set => SetValue(BarGapProperty, value); }

    public override void Render(DrawingContext context)
    {
        var (w, h) = (Bounds.Width, Bounds.Height);
        if (w <= 0 || h <= 0) return;
        var brush = Brush;

        switch (Style)
        {
            case SpectrumStyle.Capsule:
                foreach (var r in SpectrumGeometry.Capsules(Bands, w, h, BarGap)) DrawCapsule(context, brush, r);
                break;

            case SpectrumStyle.PeakCap:
                DrawPeakCap(context, brush, w, h);
                break;

            case SpectrumStyle.Ridge:
                DrawRidge(context, brush, w, h);
                break;

            case SpectrumStyle.Spectrogram:
                var color = ColorOf(brush);
                foreach (var (cell, value) in SpectrumGeometry.SpectrogramCells(History, SpectrumGeometry.SpectrogramColumns, w, h))
                {
                    context.FillRectangle(new SolidColorBrush(color, RestOpacity + (1 - RestOpacity) * value), cell);
                }

                break;

            case SpectrumStyle.Tri:
                IReadOnlyList<float> tri = Bands.Count >= 3 ? Bands : [0f, 0f, 0f];
                var dots = SpectrumGeometry.Tri(tri[0], tri[1], tri[2], w, h);
                for (var k = 0; k < 3; k++)
                {
                    DrawCircle(context, WithOpacity(brush, 0.4 + 0.6 * SpectrumGeometry.Clamp01(tri[k])), dots[k].Center, dots[k].Radius);
                }

                break;

            case SpectrumStyle.Chroma:
                DrawChroma(context, brush, w, h);
                break;
        }
    }

    /// <summary>
    /// 柱体从基线向外由实色渐隐到 35%；峰值帽用白色 90%，与柱体区分开。
    /// 镜像时基线是中线，渐变从中线起向上铺、以 Reflect 关于起点翻折到下半根——否则下半根会是实心的。
    /// </summary>
    private void DrawPeakCap(DrawingContext context, IBrush brush, double w, double h)
    {
        var color = ColorOf(brush);
        var gradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, Mirrored ? 0.5 : 1, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            SpreadMethod = Mirrored ? GradientSpreadMethod.Reflect : GradientSpreadMethod.Pad,
            GradientStops = { new GradientStop(color, 0), new GradientStop(Color.FromArgb((byte)(color.A * 0.35), color.R, color.G, color.B), 1) }
        };
        foreach (var bar in SpectrumGeometry.Bars(Bands, w, h, BarGap, Mirrored))
        {
            if (bar.Width > 0 && bar.Height > 0) context.DrawRectangle(gradient, null, bar, 1, 1);
        }

        var capBrush = new SolidColorBrush(Colors.White, 0.9);
        foreach (var cap in SpectrumGeometry.PeakCaps(Peaks, w, h, BarGap, Mirrored)) context.FillRectangle(capBrush, cap);
    }

    private void DrawRidge(DrawingContext context, IBrush brush, double w, double h)
    {
        var points = SpectrumGeometry.Ridge(Bands, w, h, Mirrored);
        var color = ColorOf(brush);
        var line = BuildCurve(points, closeTo: null);
        var fill = BuildCurve(points, closeTo: Mirrored ? h / 2 : h);
        var fade = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.FromArgb((byte)(color.A * 0.55), color.R, color.G, color.B), 0), new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1) }
        };
        var pen = new Pen(brush, 1.5, lineJoin: PenLineJoin.Round);

        context.DrawGeometry(fade, null, fill);
        context.DrawGeometry(null, pen, line);
        if (!Mirrored) return;

        // 关于水平中线再画一遍。
        using (context.PushTransform(Matrix.CreateScale(1, -1) * Matrix.CreateTranslation(0, h)))
        {
            context.DrawGeometry(fade, null, fill);
            context.DrawGeometry(null, pen, line);
        }
    }

    private static StreamGeometry BuildCurve(Point[] points, double? closeTo)
    {
        var geometry = new StreamGeometry();
        using var sink = geometry.Open();
        sink.BeginFigure(points[0], closeTo is not null);
        for (var k = 1; k < points.Length; k++)
        {
            var (p0, p1) = (points[k - 1], points[k]);
            var mx = (p0.X + p1.X) / 2;
            sink.CubicBezierTo(new Point(mx, p0.Y), new Point(mx, p1.Y), p1);
        }

        if (closeTo is { } y)
        {
            sink.LineTo(new Point(points[^1].X, y));
            sink.LineTo(new Point(points[0].X, y));
        }

        sink.EndFigure(closeTo is not null);
        return geometry;
    }

    private void DrawChroma(DrawingContext context, IBrush brush, double w, double h)
    {
        var (center, radius, thickness, arcs) = SpectrumGeometry.Chroma(Bands, w, h);
        if (radius <= 0) return;
        foreach (var (start, sweep, value) in arcs)
        {
            var geometry = new StreamGeometry();
            using (var sink = geometry.Open())
            {
                sink.BeginFigure(PointOn(center, radius, start), false);
                sink.ArcTo(PointOn(center, radius, start + sweep), new Size(radius, radius), 0, false, SweepDirection.Clockwise);
                sink.EndFigure(false);
            }

            var pen = new Pen(WithOpacity(brush, RestOpacity + (1 - RestOpacity) * value), thickness, lineCap: PenLineCap.Round);
            context.DrawGeometry(null, pen, geometry);
        }
    }

    private static Point PointOn(Point center, double radius, double degrees)
    {
        var rad = degrees * Math.PI / 180;
        return new Point(center.X + radius * Math.Cos(rad), center.Y + radius * Math.Sin(rad));
    }
}
