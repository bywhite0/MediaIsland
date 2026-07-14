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
    private const double EndingEmphasisMinimumDurationMilliseconds = 1000;
    private const double EndingEmphasisScaleAmplitude = 0.045;

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

    public static readonly StyledProperty<bool> IsWordLiftEnabledProperty =
        AvaloniaProperty.Register<WordLyricsPresenter, bool>(nameof(IsWordLiftEnabled), true);

    public static readonly StyledProperty<bool> IsLineSpringEnabledProperty =
        AvaloniaProperty.Register<WordLyricsPresenter, bool>(nameof(IsLineSpringEnabled), true);

    public static readonly StyledProperty<bool> IsEndingEmphasisEnabledProperty =
        AvaloniaProperty.Register<WordLyricsPresenter, bool>(nameof(IsEndingEmphasisEnabled), true);

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

    public bool IsWordLiftEnabled
    {
        get => GetValue(IsWordLiftEnabledProperty);
        set => SetValue(IsWordLiftEnabledProperty, value);
    }

    public bool IsLineSpringEnabled
    {
        get => GetValue(IsLineSpringEnabledProperty);
        set => SetValue(IsLineSpringEnabledProperty, value);
    }

    public bool IsEndingEmphasisEnabled
    {
        get => GetValue(IsEndingEmphasisEnabledProperty);
        set => SetValue(IsEndingEmphasisEnabledProperty, value);
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
            IsWordLiftEnabledProperty,
            IsLineSpringEnabledProperty,
            IsEndingEmphasisEnabledProperty);
        AffectsMeasure<WordLyricsPresenter>(
            LineProperty,
            FontSizeProperty,
            FontFamilyProperty,
            ForegroundProperty,
            IsWordLiftEnabledProperty,
            IsLineSpringEnabledProperty,
            IsEndingEmphasisEnabledProperty);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        EnsureMetrics();
        if (_fullText == null)
        {
            return new Size(0, FontSize * 1.3);
        }

        var desiredWidth = _fullText.Width + GetEndingEmphasisHorizontalSpace();
        var width = double.IsFinite(availableSize.Width) && availableSize.Width > 0
            ? Math.Min(desiredWidth, availableSize.Width)
            : desiredWidth;
        var motionSpace = GetMotionSpace();
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
        var motionSpace = GetMotionSpace();
        var origin = new Point(GetHorizontalOrigin(_fullText.Width),
            Math.Max(0, (Bounds.Height - _fullText.Height + motionSpace) / 2));
        var animatedOrigin = IsLineSpringEnabled
            ? new Point(origin.X, origin.Y + GetLineLiftOffset(Position))
            : origin;

        var mutedText = CreateFormatted(Line.Text ?? string.Empty, mutedBrush);
        var activeText = CreateFormatted(Line.Text ?? string.Empty, foreground);
        if (ShouldRenderAnimatedWords())
        {
            DrawAnimatedWords(context, mutedText, activeText, animatedOrigin, Position);
            return;
        }

        // 先用灰色绘制整行歌词，再覆盖已播放部分
        context.DrawText(mutedText, animatedOrigin);

        var filledWidth = ComputeFilledWidth(Position);
        if (filledWidth <= 0)
        {
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

    private bool ShouldRenderAnimatedWords()
    {
        return Line is { Words.Count: > 0 } &&
               _wordStarts.Length == Line.Words.Count &&
               (IsWordLiftEnabled || (IsEndingEmphasisEnabled && HasLongEndingWord()));
    }

    private void DrawAnimatedWords(
        DrawingContext context,
        FormattedText mutedText,
        FormattedText activeText,
        Point origin,
        TimeSpan position)
    {
        var words = Line!.Words;
        for (var i = 0; i < words.Count; i++)
        {
            var progress = GetProgress(position, words[i].StartTime, words[i].EndTime);
            var liftOffset = IsWordLiftEnabled ? GetWordLiftOffset(progress) : 0;
            var wordOrigin = new Point(origin.X, origin.Y + liftOffset);
            var scale = GetEndingEmphasisScale(i, position);
            DrawWordLayers(
                context,
                mutedText,
                activeText,
                wordOrigin,
                _wordStarts[i],
                _wordWidths[i],
                progress,
                scale);
        }
    }

    private void DrawWordLayers(
        DrawingContext context,
        FormattedText mutedText,
        FormattedText activeText,
        Point origin,
        double wordStart,
        double wordWidth,
        double progress,
        double scale)
    {
        if (scale > 1.0001)
        {
            var center = new Point(
                origin.X + wordStart + (wordWidth / 2),
                origin.Y + (_fullText!.Height / 2));
            using (context.PushTransform(CreateScaleAround(scale, center)))
            {
                DrawWordLayersCore(context, mutedText, activeText, origin, wordStart, wordWidth, progress);
            }

            return;
        }

        DrawWordLayersCore(context, mutedText, activeText, origin, wordStart, wordWidth, progress);
    }

    private void DrawWordLayersCore(
        DrawingContext context,
        FormattedText mutedText,
        FormattedText activeText,
        Point origin,
        double wordStart,
        double wordWidth,
        double progress)
    {
        var wordClip = new Rect(origin.X + wordStart, 0, wordWidth, Bounds.Height);
        using (context.PushClip(wordClip))
        {
            // 暗色底字与高亮层共用同一位移和缩放，避免上浮后残留重影
            context.DrawText(mutedText, origin);
            if (progress >= 1)
            {
                context.DrawText(activeText, origin);
                return;
            }

            if (progress > 0)
            {
                DrawFeatheredWord(context, activeText, origin, wordStart, wordWidth, progress);
            }
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
        using (context.PushClip(maskBounds))
        using (context.PushOpacityMask(edgeMask, maskBounds))
        {
            context.DrawText(activeText, lineOrigin);
        }
    }

    private double GetLineLiftDistance()
    {
        return Math.Clamp(FontSize * 0.12, 1.25, 2.0);
    }

    private double GetMotionSpace()
    {
        double motionSpace = 0;
        if (IsLineSpringEnabled)
        {
            motionSpace += GetLineLiftDistance() * (1 + SpringOvershootRatio);
        }

        if (IsWordLiftEnabled)
        {
            motionSpace += GetWordLiftDistance();
        }

        if (IsEndingEmphasisEnabled && HasLongEndingWord())
        {
            motionSpace += (_fullText?.Height ?? FontSize) * EndingEmphasisScaleAmplitude;
        }

        return motionSpace > 0 ? motionSpace + GetBaselineOffset() : 0;
    }

    private double GetLineLiftOffset(TimeSpan position)
    {
        return -GetLineLiftDistance() * GetLineSpringResponse(
            position,
            Line!.StartTime,
            Line.EndTime);
    }

    internal static double GetLineSpringResponse(TimeSpan position, TimeSpan start, TimeSpan end)
    {
        return end <= start ? 0 : GetSpringResponse(GetProgress(position, start, end));
    }

    internal static double GetSpringResponse(double progress)
    {
        // 以终点值归一化，避免阻尼余量使动画无法精确停在终点
        progress = Math.Clamp(progress, 0, 1);
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

    private bool HasLongEndingWord()
    {
        var words = Line?.Words;
        return words is { Count: > 0 } &&
               (words[^1].EndTime - words[^1].StartTime).TotalMilliseconds >
               EndingEmphasisMinimumDurationMilliseconds;
    }

    private double GetEndingEmphasisHorizontalSpace()
    {
        return IsEndingEmphasisEnabled && HasLongEndingWord() && _wordWidths.Length > 0
            ? _wordWidths[^1] * EndingEmphasisScaleAmplitude
            : 0;
    }

    private double GetEndingEmphasisScale(int wordIndex, TimeSpan position)
    {
        if (!IsEndingEmphasisEnabled || Line == null || wordIndex != Line.Words.Count - 1)
        {
            return 1;
        }

        return GetEndingEmphasisScale(position, Line.Words[wordIndex]);
    }

    internal static double GetEndingEmphasisScale(TimeSpan position, LyricsWord word)
    {
        var duration = word.EndTime - word.StartTime;
        if (duration.TotalMilliseconds <= EndingEmphasisMinimumDurationMilliseconds ||
            position <= word.StartTime ||
            position >= word.EndTime)
        {
            return 1;
        }

        var progress = GetProgress(position, word.StartTime, word.EndTime);
        var breathingEnvelope = Math.Sin(Math.PI * progress);
        return 1 + (EndingEmphasisScaleAmplitude * breathingEnvelope);
    }

    internal static Matrix CreateScaleAround(double scale, Point center)
    {
        return Matrix.CreateTranslation(-center.X, -center.Y)
            .Append(Matrix.CreateScale(scale, scale))
            .Append(Matrix.CreateTranslation(center.X, center.Y));
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
