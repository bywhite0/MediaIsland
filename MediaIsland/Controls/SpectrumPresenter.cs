using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MediaIsland.Controls;

/// <summary>
/// 频谱渲染。四种形态共用一次 Render，按 Mode 分派——它们的几何成本同阶
/// （几十个矩形或一条折线），共用一条矢量绘制路径即可，无需为切换形态重建控件。
///
/// 属性由组件的定时器逐帧写入，AffectsRender 触发重绘；与 InterludeDotsPresenter
/// 同构，不额外创建 Avalonia 动画对象。所有几何计算都委托给 SpectrumGeometry，
/// 本类只负责取笔刷、分派形态与调用绘制 API——那部分才是无 UI 环境测不了的。
/// </summary>
public sealed class SpectrumPresenter : Control
{
    public static readonly StyledProperty<SpectrumVisualMode> ModeProperty =
        AvaloniaProperty.Register<SpectrumPresenter, SpectrumVisualMode>(nameof(Mode));

    public static readonly StyledProperty<IReadOnlyList<float>> BandsProperty =
        AvaloniaProperty.Register<SpectrumPresenter, IReadOnlyList<float>>(nameof(Bands), []);

    public static readonly StyledProperty<IReadOnlyList<float>> WaveformProperty =
        AvaloniaProperty.Register<SpectrumPresenter, IReadOnlyList<float>>(nameof(Waveform), []);

    public static readonly StyledProperty<double> RmsProperty =
        AvaloniaProperty.Register<SpectrumPresenter, double>(nameof(Rms));

    public static readonly StyledProperty<double> PeakProperty =
        AvaloniaProperty.Register<SpectrumPresenter, double>(nameof(Peak));

    public static readonly StyledProperty<double> PulseEnergyProperty =
        AvaloniaProperty.Register<SpectrumPresenter, double>(nameof(PulseEnergy));

    public static readonly StyledProperty<bool> MirroredProperty =
        AvaloniaProperty.Register<SpectrumPresenter, bool>(nameof(Mirrored));

    public static readonly StyledProperty<double> BarGapProperty =
        AvaloniaProperty.Register<SpectrumPresenter, double>(nameof(BarGap), 2);

    public static readonly StyledProperty<double> BarCornerRadiusProperty =
        AvaloniaProperty.Register<SpectrumPresenter, double>(nameof(BarCornerRadius), 1);

    /// <summary>
    /// 前景笔刷。声明为继承属性，好让组件用「清除本地值即回落主题色」这一招——
    /// 与既有进度条的做法同构：要主题色时 ClearValue，要自定义色时显式赋值。
    /// </summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<SpectrumPresenter, IBrush?>(nameof(Foreground), inherits: true);

    static SpectrumPresenter() =>
        AffectsRender<SpectrumPresenter>(
            ModeProperty, BandsProperty, WaveformProperty, RmsProperty, PeakProperty,
            PulseEnergyProperty, MirroredProperty, BarGapProperty, BarCornerRadiusProperty,
            ForegroundProperty);

    public SpectrumVisualMode Mode
    {
        get => GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public IReadOnlyList<float> Bands
    {
        get => GetValue(BandsProperty);
        set => SetValue(BandsProperty, value);
    }

    public IReadOnlyList<float> Waveform
    {
        get => GetValue(WaveformProperty);
        set => SetValue(WaveformProperty, value);
    }

    public double Rms
    {
        get => GetValue(RmsProperty);
        set => SetValue(RmsProperty, value);
    }

    public double Peak
    {
        get => GetValue(PeakProperty);
        set => SetValue(PeakProperty, value);
    }

    public double PulseEnergy
    {
        get => GetValue(PulseEnergyProperty);
        set => SetValue(PulseEnergyProperty, value);
    }

    public bool Mirrored
    {
        get => GetValue(MirroredProperty);
        set => SetValue(MirroredProperty, value);
    }

    public double BarGap
    {
        get => GetValue(BarGapProperty);
        set => SetValue(BarGapProperty, value);
    }

    public double BarCornerRadius
    {
        get => GetValue(BarCornerRadiusProperty);
        set => SetValue(BarCornerRadiusProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var brush = Foreground ?? Brushes.White;
        var width = Bounds.Width;
        var height = Bounds.Height;
        // 布局完成前 Bounds 是 0，此时任何绘制都没有意义。
        if (width <= 0 || height <= 0)
        {
            return;
        }

        switch (Mode)
        {
            case SpectrumVisualMode.Pulse:
                DrawRounded(context, brush, SpectrumGeometry.Pulse(PulseEnergy, width, height, Mirrored));
                break;

            case SpectrumVisualMode.Bars:
                foreach (var bar in SpectrumGeometry.Bars(Bands, width, height, BarGap, Mirrored))
                {
                    DrawRounded(context, brush, bar);
                }

                break;

            case SpectrumVisualMode.Oscilloscope:
                DrawWaveform(context, brush, width, height);
                break;

            case SpectrumVisualMode.LevelMeter:
                var (fill, tick) = SpectrumGeometry.LevelMeter(Rms, Peak, width, height);
                DrawRounded(context, brush, fill);
                context.FillRectangle(brush, tick);
                break;
        }
    }

    private void DrawRounded(DrawingContext context, IBrush brush, Rect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        // 圆角不得超过矩形半高，否则低幅度时会画出橄榄形而非细条。
        var radius = Math.Min(BarCornerRadius, Math.Min(rect.Width, rect.Height) / 2);
        context.DrawRectangle(brush, null, rect, radius, radius);
    }

    private void DrawWaveform(DrawingContext context, IBrush brush, double width, double height)
    {
        var points = SpectrumGeometry.Oscilloscope(Waveform, width, height);
        // 少于两点画不出折线。帧流中断时组件直接给空数组，这里就是那条路径的落点。
        if (points.Length < 2)
        {
            return;
        }

        var geometry = new StreamGeometry();
        using (var sink = geometry.Open())
        {
            sink.BeginFigure(points[0], false);
            for (var i = 1; i < points.Length; i++)
            {
                sink.LineTo(points[i]);
            }

            sink.EndFigure(false);
        }

        context.DrawGeometry(null, new Pen(brush, 1.5), geometry);
    }
}
