using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MediaIsland.Services.Lyrics.Models;

namespace MediaIsland.Components;

/// <summary>
/// Custom single-line word-synced lyric renderer.
/// </summary>
public sealed class WordLyricsPresenter : Control
{
    public static readonly StyledProperty<LyricsLine?> LineProperty =
        AvaloniaProperty.Register<WordLyricsPresenter, LyricsLine?>(nameof(Line));

    public static readonly StyledProperty<TimeSpan> PositionProperty =
        AvaloniaProperty.Register<WordLyricsPresenter, TimeSpan>(nameof(Position));

    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<WordLyricsPresenter, double>(nameof(FontSize), 14);

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<WordLyricsPresenter, IBrush?>(nameof(Foreground));

    public static readonly StyledProperty<TextAlignment> TextAlignmentProperty =
        AvaloniaProperty.Register<WordLyricsPresenter, TextAlignment>(nameof(TextAlignment), TextAlignment.Center);

    private string? _cachedText;
    private double _cachedFontSize;
    private Typeface _typeface = new(FontFamily.Default);
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

    static WordLyricsPresenter()
    {
        AffectsRender<WordLyricsPresenter>(
            LineProperty,
            PositionProperty,
            FontSizeProperty,
            ForegroundProperty,
            TextAlignmentProperty);
        AffectsMeasure<WordLyricsPresenter>(LineProperty, FontSizeProperty, ForegroundProperty);
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
        return new Size(width, Math.Max(_fullText.Height, FontSize * 1.2));
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
        var origin = new Point(GetHorizontalOrigin(_fullText.Width),
            Math.Max(0, (Bounds.Height - _fullText.Height) / 2));

        var mutedText = CreateFormatted(Line.Text ?? string.Empty, mutedBrush);
        context.DrawText(mutedText, origin);

        var filledWidth = ComputeFilledWidth(Position);
        if (filledWidth <= 0)
        {
            return;
        }

        var activeText = CreateFormatted(Line.Text ?? string.Empty, foreground);
        var clipRect = new Rect(origin.X, origin.Y, filledWidth, Math.Max(activeText.Height, Bounds.Height));
        using (context.PushClip(clipRect))
        {
            context.DrawText(activeText, origin);
        }
    }

    private double ComputeFilledWidth(TimeSpan position)
    {
        var words = Line?.Words;
        if (words == null || words.Count == 0 || _wordStarts.Length != words.Count)
        {
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
            Math.Abs(_cachedFontSize - FontSize) < 0.01)
        {
            return;
        }

        _cachedText = text;
        _cachedFontSize = FontSize;
        _typeface = new Typeface(FontFamily.Default);
        var brush = Foreground ?? Brushes.White;
        _fullText = CreateFormatted(text, brush);
        BuildWordMetrics(line, brush);
    }

    private void BuildWordMetrics(LyricsLine? line, IBrush brush)
    {
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
            _wordWidths[i] = layout.WidthIncludingTrailingWhitespace;
            if (_wordWidths[i] <= 0)
            {
                _wordWidths[i] = layout.Width;
            }

            cursor += _wordWidths[i];
        }

        if (_fullText != null && cursor > 0 && Math.Abs(_fullText.Width - cursor) > 1)
        {
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
