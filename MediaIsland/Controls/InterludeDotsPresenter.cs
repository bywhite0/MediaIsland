using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MediaIsland.Controls;

/// <summary>
/// 在同步歌词的长间奏期间渲染三个提示点，并根据播放时间计算呼吸、淡入和淡出状态。
/// </summary>
public sealed class InterludeDotsPresenter : Control
{
    // 动画时长单位均为毫秒；呼吸周期会按实际间奏时长切分，避免短间奏只出现半个周期。
    private const double TargetBreatheDurationMilliseconds = 1500;
    private const double EntranceDurationMilliseconds = 2000;
    private const double EntranceDelayMilliseconds = 500;
    private const double EntranceFadeDurationMilliseconds = 500;
    private const double ExitDurationMilliseconds = 750;
    private const double ExitFadeDurationMilliseconds = 375;

    public static readonly StyledProperty<TimeSpan> StartTimeProperty =
        AvaloniaProperty.Register<InterludeDotsPresenter, TimeSpan>(nameof(StartTime));

    public static readonly StyledProperty<TimeSpan> EndTimeProperty =
        AvaloniaProperty.Register<InterludeDotsPresenter, TimeSpan>(nameof(EndTime));

    public static readonly StyledProperty<TimeSpan> PositionProperty =
        AvaloniaProperty.Register<InterludeDotsPresenter, TimeSpan>(nameof(Position));

    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<InterludeDotsPresenter, double>(nameof(FontSize), 14);

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<InterludeDotsPresenter, IBrush?>(nameof(Foreground));

    // 这三个时间属性由歌词组件逐帧更新，属性变更只触发本控件重绘，不额外创建 Avalonia 动画对象。
    public TimeSpan StartTime
    {
        get => GetValue(StartTimeProperty);
        set => SetValue(StartTimeProperty, value);
    }

    public TimeSpan EndTime
    {
        get => GetValue(EndTimeProperty);
        set => SetValue(EndTimeProperty, value);
    }

    public TimeSpan Position
    {
        get => GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    static InterludeDotsPresenter()
    {
        AffectsRender<InterludeDotsPresenter>(
            StartTimeProperty,
            EndTimeProperty,
            PositionProperty,
            FontSizeProperty,
            ForegroundProperty);
        AffectsMeasure<InterludeDotsPresenter>(FontSizeProperty);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // 为放大后的点和点间距预留空间，避免呼吸缩放在边缘被父控件裁切。
        var radius = GetDotRadius(FontSize);
        var spacing = GetDotSpacing(radius);
        return new Size(
            (radius * 2 + spacing * 2) * 1.1,
            Math.Max(FontSize * 1.2, radius * 2 * 1.1));
    }

    public override void Render(DrawingContext context)
    {
        var state = GetAnimationState(StartTime, EndTime, Position);
        if (!state.IsVisible || state.Scale <= 0)
        {
            return;
        }

        var brush = Foreground ?? Brushes.White;
        var radius = GetDotRadius(FontSize) * state.Scale;
        var spacing = GetDotSpacing(radius);
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);

        // 三个点共享同一条缩放和基线，只通过透明度依次出现，避免产生跳动的逐点位移动画。
        DrawDot(context, brush, new Point(center.X - spacing, center.Y), radius, state.FirstDotOpacity);
        DrawDot(context, brush, center, radius, state.SecondDotOpacity);
        DrawDot(context, brush, new Point(center.X + spacing, center.Y), radius, state.ThirdDotOpacity);
    }

    internal static InterludeDotsAnimationState GetAnimationState(
        TimeSpan startTime,
        TimeSpan endTime,
        TimeSpan position)
    {
        var interludeDuration = endTime - startTime;
        var currentDuration = position - startTime;
        if (interludeDuration <= TimeSpan.Zero ||
            currentDuration < TimeSpan.Zero ||
            currentDuration > interludeDuration)
        {
            return default;
        }

        var interludeDurationMilliseconds = interludeDuration.TotalMilliseconds;
        var currentDurationMilliseconds = currentDuration.TotalMilliseconds;
        var remainingDurationMilliseconds = interludeDurationMilliseconds - currentDurationMilliseconds;
        // 按目标 1.5 秒周期向上取整，再均分总时长，使最后一个完整呼吸周期恰好落在间奏内。
        var breatheDuration = interludeDurationMilliseconds /
                              Math.Ceiling(interludeDurationMilliseconds / TargetBreatheDurationMilliseconds);
        // 使用正弦波提供连续的整体呼吸缩放。
        var scale = Math.Sin(
                        1.5 * Math.PI - (currentDurationMilliseconds / breatheDuration) * 2) /
                    20 +
                    1;
        var globalOpacity = 1d;

        if (currentDurationMilliseconds < EntranceDurationMilliseconds)
        {
            // 前两秒采用指数缓出，让提示点从无到有时更柔和。
            scale *= EaseOutExpo(currentDurationMilliseconds / EntranceDurationMilliseconds);
        }

        // 前 500ms 保持不可见，随后用 500ms 淡入，避免刚进入间奏就突兀闪现。
        if (currentDurationMilliseconds < EntranceDelayMilliseconds)
        {
            globalOpacity = 0;
        }
        else if (currentDurationMilliseconds < EntranceDelayMilliseconds + EntranceFadeDurationMilliseconds)
        {
            globalOpacity *= (currentDurationMilliseconds - EntranceDelayMilliseconds) /
                             EntranceFadeDurationMilliseconds;
        }

        if (remainingDurationMilliseconds < ExitDurationMilliseconds)
        {
            // 末段缩小并交给下一句歌词的预显示状态衔接。
            scale *= 1 - EaseInOutBack(
                (ExitDurationMilliseconds - remainingDurationMilliseconds) /
                ExitDurationMilliseconds /
                2);
        }

        if (remainingDurationMilliseconds < ExitFadeDurationMilliseconds)
        {
            globalOpacity *= Math.Clamp(
                remainingDurationMilliseconds / ExitFadeDurationMilliseconds,
                0,
                1);
        }

        var dotsDuration = Math.Max(1, interludeDurationMilliseconds - ExitDurationMilliseconds);
        return new InterludeDotsAnimationState(
            true,
            Math.Max(0, scale) * 0.82,
            Math.Clamp(globalOpacity * GetDotOpacity(currentDurationMilliseconds, dotsDuration, 0), 0, 1),
            Math.Clamp(globalOpacity * GetDotOpacity(currentDurationMilliseconds, dotsDuration, 1), 0, 1),
            Math.Clamp(globalOpacity * GetDotOpacity(currentDurationMilliseconds, dotsDuration, 2), 0, 1));
    }

    private static void DrawDot(
        DrawingContext context,
        IBrush brush,
        Point center,
        double radius,
        double opacity)
    {
        if (opacity <= 0)
        {
            return;
        }

        using (context.PushOpacity(opacity))
        {
            context.DrawEllipse(brush, null, center, radius, radius);
        }
    }

    private static double GetDotRadius(double fontSize) => Math.Clamp(fontSize * 0.22, 2, 5.5);

    private static double GetDotSpacing(double radius) => radius * 3.3;

    private static double GetDotOpacity(double currentDuration, double dotsDuration, int index)
    {
        // 三个点在可见期按 1/3 时差依次淡入，最低透明度保持在 0.25，避免后两个点完全消失太久。
        return Math.Clamp(
            ((currentDuration - (dotsDuration / 3 * index)) * 3 / dotsDuration) * 0.75,
            0.25,
            1);
    }

    private static double EaseOutExpo(double progress)
    {
        if (progress <= 0)
        {
            return 0;
        }

        if (progress >= 1)
        {
            return 1;
        }

        return 1 - Math.Pow(2, -10 * progress);
    }

    private static double EaseInOutBack(double progress)
    {
        var clampedProgress = Math.Clamp(progress, 0, 1);
        const double overshoot = 1.70158;
        var factor = overshoot * 1.525;
        return clampedProgress < 0.5
            ? Math.Pow(2 * clampedProgress, 2) * ((factor + 1) * 2 * clampedProgress - factor) / 2
            : (Math.Pow(2 * clampedProgress - 2, 2) * ((factor + 1) * (clampedProgress * 2 - 2) + factor) + 2) / 2;
    }
}

internal readonly record struct InterludeDotsAnimationState(
    bool IsVisible,
    double Scale,
    double FirstDotOpacity,
    double SecondDotOpacity,
    double ThirdDotOpacity);
