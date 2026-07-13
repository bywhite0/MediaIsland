using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using MediaIsland.Services.Lyrics.Models;

namespace MediaIsland.Components;

/// <summary>
/// 自定义的单行逐字歌词渲染器。
/// </summary>
public sealed class WordLyricsPresenter : Control
{
    private const double SpringDamping = 5.5;
    private const double SpringAngularFrequency = 9.5;
    private const double SpringOvershootRatio = 0.2;
    private const double LineSpringDurationMilliseconds = 480;

    public static readonly StyledProperty<LyricsLine?> LineProperty =
        AvaloniaProperty.Register<WordLyricsPresenter, LyricsLine?>(nameof(Line));

    public static readonly StyledProperty<TimeSpan> PositionProperty =
        AvaloniaProperty.Register<WordLyricsPresenter, TimeSpan>(nameof(Position));

    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<WordLyricsPresenter, double>(nameof(FontSize), 14);

    // 继承 ClassIsland 的自定义字体，确保歌词字体与普通文本一致
    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        TextElement.FontFamilyProperty.AddOwner<WordLyricsPresenter>();

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<WordLyricsPresenter, IBrush?>(nameof(Foreground));

    public static readonly StyledProperty<TextAlignment> TextAlignmentProperty =
        AvaloniaProperty.Register<WordLyricsPresenter, TextAlignment>(nameof(TextAlignment), TextAlignment.Center);

    public static readonly StyledProperty<bool> IsLiftEnabledProperty =
        AvaloniaProperty.Register<WordLyricsPresenter, bool>(nameof(IsLiftEnabled), true);

    // 缓存当前排版结果，避免播放期间逐帧重复测量
    private string? _cachedText;
    private double _cachedFontSize;
    private FontFamily _cachedFontFamily = Avalonia.Media.FontFamily.Default;
    private Typeface _typeface = new(Avalonia.Media.FontFamily.Default);
    private FormattedText? _fullText;
    private double[] _wordStarts = [];
    private double[] _wordWidths = [];

    public LyricsLine? Line
    {
        get => GetValue(LineProperty);
        set => SetValue(LineProperty, value);
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

    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public TextAlignment TextAlignment
    {
        get => GetValue(TextAlignmentProperty);
        set => SetValue(TextAlignmentProperty, value);
    }

    public bool IsLiftEnabled
    {
        get => GetValue(IsLiftEnabledProperty);
        set => SetValue(IsLiftEnabledProperty, value);
    }

    static WordLyricsPresenter()
    {
        // 排版相关属性改变时，需要重新测量和绘制
        AffectsRender<WordLyricsPresenter>(
            LineProperty,
            PositionProperty,
            FontSizeProperty,
            FontFamilyProperty,
            ForegroundProperty,
            TextAlignmentProperty,
            IsLiftEnabledProperty);
        AffectsMeasure<WordLyricsPresenter>(
            LineProperty,
            FontSizeProperty,
            FontFamilyProperty,
            ForegroundProperty,
            IsLiftEnabledProperty);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        EnsureMetrics();
        if (_fullText == null)
        {
            return new Size(0, FontSize * 1.3);
        }

        var width = double.IsFinite(availableSize.Width) && availableSize.Width > 0
            ? Math.Min(_fullText.Width, availableSize.Width)
            : _fullText.Width;
        var motionSpace = IsLiftEnabled ? GetMotionSpace() : 0;
        return new Size(width, Math.Max(_fullText.Height, FontSize * 1.2) + motionSpace);
    }

    public override void Render(DrawingContext context)
    {
        EnsureMetrics();
        if (_fullText == null || Line == null)
        {
            return;
        }

        var foreground = Foreground ?? Brushes.White;
        var mutedBrush = CreateMutedBrush(foreground);
        var motionSpace = IsLiftEnabled ? GetMotionSpace() : 0;
        var origin = new Point(GetHorizontalOrigin(_fullText.Width),
            Math.Max(0, (Bounds.Height - _fullText.Height + motionSpace) / 2));
        var animatedOrigin = IsLiftEnabled
            ? new Point(origin.X, origin.Y + GetLineLiftOffset(Position))
            : origin;

        // 先用灰色绘制整行歌词，再覆盖已播放部分
        var mutedText = CreateFormatted(Line.Text ?? string.Empty, mutedBrush);
        context.DrawText(mutedText, animatedOrigin);

        var filledWidth = ComputeFilledWidth(Position);
        if (filledWidth <= 0)
        {
            return;
        }

        var activeText = CreateFormatted(Line.Text ?? string.Empty, foreground);
        if (IsLiftEnabled && Line.Words.Count > 0 && _wordStarts.Length == Line.Words.Count)
        {
            DrawAnimatedHighlight(context, activeText, animatedOrigin, Position);
            return;
        }

        var clipRect = new Rect(
            animatedOrigin.X,
            animatedOrigin.Y,
            filledWidth,
            Math.Max(activeText.Height, Bounds.Height));
        using (context.PushClip(clipRect))
        {
            context.DrawText(activeText, animatedOrigin);
        }
    }

    private void DrawAnimatedHighlight(
        DrawingContext context,
        FormattedText activeText,
        Point origin,
        TimeSpan position)
    {
        // 已完成词整体抬升，当前词使用羽化裁剪
        var words = Line!.Words;
        double completedWidth = 0;
        for (var i = 0; i < words.Count; i++)
        {
            var progress = GetProgress(position, words[i].StartTime, words[i].EndTime);
            if (progress <= 0)
            {
                break;
            }

            if (progress >= 1)
            {
                completedWidth = _wordStarts[i] + _wordWidths[i];
            }
        }

        if (completedWidth > 0)
        {
            var completedClip = new Rect(origin.X, 0, completedWidth, Bounds.Height);
            var completedOrigin = new Point(origin.X, origin.Y - GetWordLiftDistance());
            using (context.PushClip(completedClip))
            {
                context.DrawText(activeText, completedOrigin);
            }
        }

        for (var i = 0; i < words.Count; i++)
        {
            var progress = GetProgress(position, words[i].StartTime, words[i].EndTime);
            if (progress <= 0)
            {
                break;
            }

            if (progress >= 1)
            {
                continue;
            }

            DrawFeatheredWord(
                context,
                activeText,
                origin,
                _wordStarts[i],
                _wordWidths[i],
                progress);
        }
    }

    private void DrawFeatheredWord(
        DrawingContext context,
        FormattedText activeText,
        Point lineOrigin,
        double wordStart,
        double wordWidth,
        double progress)
    {
        // 渐变边缘向未填充区域延伸，避免进度跳动时出现硬切线
        var filledWidth = wordWidth * progress;
        var edgeWidth = Math.Clamp(FontSize * 0.45, 4.0, 7.0);
        var opaqueWidth = Math.Max(0, filledWidth - (edgeWidth * 0.35));
        var maskedWidth = Math.Min(wordWidth, filledWidth + (edgeWidth * 0.65));
        if (maskedWidth <= 0)
        {
            return;
        }

        var fadeStart = Math.Clamp(opaqueWidth / maskedWidth, 0, 1);
        var edgeMask = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative)
        };
        edgeMask.GradientStops.Add(new GradientStop(Colors.White, 0));
        edgeMask.GradientStops.Add(new GradientStop(Colors.White, fadeStart));
        edgeMask.GradientStops.Add(new GradientStop(Colors.Transparent, 1));

        var maskBounds = new Rect(lineOrigin.X + wordStart, 0, maskedWidth, Bounds.Height);
        var wordOrigin = new Point(lineOrigin.X, lineOrigin.Y + GetWordLiftOffset(progress));
        using (context.PushClip(maskBounds))
        using (context.PushOpacityMask(edgeMask, maskBounds))
        {
            context.DrawText(activeText, wordOrigin);
        }
    }

    private double GetLiftDistance()
    {
        return Math.Clamp(FontSize * 0.12, 1.25, 2.0);
    }

    private double GetMotionSpace()
    {
        return (GetLiftDistance() * (1 + SpringOvershootRatio)) +
               GetWordLiftDistance() +
               GetBaselineOffset();
    }

    private double GetLineLiftOffset(TimeSpan position)
    {
        var elapsed = Math.Max(0, (position - Line!.StartTime).TotalMilliseconds);
        var progress = Math.Clamp(elapsed / LineSpringDurationMilliseconds, 0, 1);
        return -GetLiftDistance() * GetSpringResponse(progress);
    }

    private static double GetSpringResponse(double progress)
    {
        // 以终点值归一化，避免阻尼余量使动画无法精确停在终点
        var response = 1 - (Math.Exp(-SpringDamping * progress) *
                            Math.Cos(SpringAngularFrequency * progress));
        var finalResponse = 1 - (Math.Exp(-SpringDamping) * Math.Cos(SpringAngularFrequency));
        return response / finalResponse;
    }

    private double GetWordLiftDistance()
    {
        return Math.Clamp(FontSize * 0.07, 0.75, 1.15);
    }

    private double GetWordLiftOffset(double progress)
    {
        var normalized = Math.Clamp(progress, 0, 1);
        var easedProgress = 1 - Math.Pow(1 - normalized, 3);
        return -GetWordLiftDistance() * easedProgress;
    }

    private double GetBaselineOffset()
    {
        return Math.Clamp(FontSize * 0.06, 0.75, 1.25);
    }

    private double ComputeFilledWidth(TimeSpan position)
    {
        var words = Line?.Words;
        if (words == null || words.Count == 0 || _wordStarts.Length != words.Count)
        {
            // 没有逐字时间轴时，按整行进度回退
            if (_fullText == null || Line == null)
            {
                return 0;
            }

            if (position < Line.StartTime)
            {
                return 0;
            }

            if (Line.EndTime <= Line.StartTime || position >= Line.EndTime)
            {
                return _fullText.Width;
            }

            var lineProgress = (position - Line.StartTime).TotalMilliseconds /
                               (Line.EndTime - Line.StartTime).TotalMilliseconds;
            return _fullText.Width * Math.Clamp(lineProgress, 0, 1);
        }

        double filled = 0;
        for (var i = 0; i < words.Count; i++)
        {
            var progress = GetProgress(position, words[i].StartTime, words[i].EndTime);
            if (progress <= 0)
            {
                break;
            }

            filled = _wordStarts[i] + (_wordWidths[i] * progress);
        }

        return filled;
    }

    private double GetHorizontalOrigin(double textWidth)
    {
        return TextAlignment switch
        {
            TextAlignment.Left or TextAlignment.Start => 0,
            TextAlignment.Right or TextAlignment.End => Math.Max(0, Bounds.Width - textWidth),
            _ => Math.Max(0, (Bounds.Width - textWidth) / 2)
        };
    }

    private void EnsureMetrics()
    {
        var line = Line;
        var text = line?.Text ?? string.Empty;
        if (_fullText != null &&
            string.Equals(_cachedText, text, StringComparison.Ordinal) &&
            Math.Abs(_cachedFontSize - FontSize) < 0.01 &&
            Equals(_cachedFontFamily, FontFamily))
        {
            return;
        }

        _cachedText = text;
        _cachedFontSize = FontSize;
        _cachedFontFamily = FontFamily;
        _typeface = new Typeface(FontFamily);
        var brush = Foreground ?? Brushes.White;
        _fullText = CreateFormatted(text, brush);
        BuildWordMetrics(line, brush);
    }

    private void BuildWordMetrics(LyricsLine? line, IBrush brush)
    {
        // 记录每个词的宽度和起点，供逐字高亮裁剪使用
        if (line == null || line.Words.Count == 0)
        {
            _wordStarts = [];
            _wordWidths = [];
            return;
        }

        _wordStarts = new double[line.Words.Count];
        _wordWidths = new double[line.Words.Count];
        double cursor = 0;
        for (var i = 0; i < line.Words.Count; i++)
        {
            var wordText = line.Words[i].Text ?? string.Empty;
            var layout = CreateFormatted(wordText, brush);
            _wordStarts[i] = cursor;
            // 保留词尾空白，避免后续词的高亮起点提前
            _wordWidths[i] = layout.WidthIncludingTrailingWhitespace;
            if (_wordWidths[i] <= 0)
            {
                _wordWidths[i] = layout.Width;
            }

            cursor += _wordWidths[i];
        }

        if (_fullText != null && cursor > 0 && Math.Abs(_fullText.Width - cursor) > 1)
        {
            // 独立词宽会受字距影响，与整行排版不一致时按整行宽度校正
            var scale = _fullText.Width / cursor;
            double scaled = 0;
            for (var i = 0; i < _wordWidths.Length; i++)
            {
                _wordStarts[i] = scaled;
                _wordWidths[i] *= scale;
                scaled += _wordWidths[i];
            }
        }
    }

    private FormattedText CreateFormatted(string text, IBrush brush)
    {
        return new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            _typeface,
            FontSize,
            brush);
    }

    private static IBrush CreateMutedBrush(IBrush foreground)
    {
        if (foreground is ISolidColorBrush solid)
        {
            return new SolidColorBrush(solid.Color, 0.38);
        }

        return foreground;
    }

    private static double GetProgress(TimeSpan position, TimeSpan start, TimeSpan end)
    {
        if (position <= start)
        {
            return 0;
        }

        if (end <= start || position >= end)
        {
            return 1;
        }

        return Math.Clamp((position - start).TotalMilliseconds / (end - start).TotalMilliseconds, 0, 1);
    }
}
