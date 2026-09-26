using Avalonia;
using Avalonia.Media;

namespace MediaIsland.Controls;

public sealed class AudioWaveformPresenter : AudioVisualPresenterBase
{
    public static readonly StyledProperty<IReadOnlyList<float>> WaveformProperty =
        AvaloniaProperty.Register<AudioWaveformPresenter, IReadOnlyList<float>>(nameof(Waveform), []);

    static AudioWaveformPresenter() => AffectsRender<AudioWaveformPresenter>(WaveformProperty);

    public IReadOnlyList<float> Waveform { get => GetValue(WaveformProperty); set => SetValue(WaveformProperty, value); }

    public override void Render(DrawingContext context)
    {
        var (w, h) = (Bounds.Width, Bounds.Height);
        if (w <= 0 || h <= 0) return;

        var points = WaveformGeometry.Oscilloscope(Waveform, w, h);
        var geometry = new StreamGeometry();
        using (var sink = geometry.Open())
        {
            sink.BeginFigure(points[0], false);
            for (var i = 1; i < points.Length; i++) sink.LineTo(points[i]);
            sink.EndFigure(false);
        }

        context.DrawGeometry(null, new Pen(Brush, 1.5), geometry);
    }
}
