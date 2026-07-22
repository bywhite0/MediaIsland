using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using MediaIsland.Services.Lyrics.Models;

namespace MediaIsland.Controls;

/// <summary>
/// 自定义的单行逐字歌词渲染器。
/// </summary>
/// <remarks>
/// 处理流程分为三步：先在 <see cref="EnsureMetrics"/> 中测量整行、单词和 Unicode 字素簇；
/// 再把单词有效时长按字符实际宽度分配给各字符；最后在渲染阶段分别计算高亮（可选边缘羽化）、持续上浮和 AMLL 强调效果，
/// 但让暗色底字与高亮文字共用同一套位移和缩放，以免动画后出现重影。
/// </remarks>
public sealed class WordLyricsPresenter : Control
{
    // AMLL 强调参数：时长决定 amount，amount 再控制缩放和位移幅度。
    // 末词仍按 AMLL 逻辑获得更强、更长的强调，但所有超过阈值的单词都可以触发效果。
    private const double WordEmphasisMinimumDurationMilliseconds = 1000;
    private const double WordEmphasisAmountFactor = 0.6;
    private const double WordEmphasisLastWordAmountFactor = 1.6;
    private const double WordEmphasisLastWordDurationFactor = 1.2;
    private const double WordEmphasisMaximumAmount = 1.2;
    private const double WordEmphasisScaleFactor = 0.1;
    private const double WordEmphasisHorizontalOffsetFactor = 0.03;
    private const double WordEmphasisVerticalOffsetFactor = 0.025;
    private const double WordEmphasisFloatOffsetFactor = 0.05;
    private const double WordEmphasisCharacterStaggerDivisor = 2.5;
    private const double MinimumCharacterLiftDurationMilliseconds = 120;

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

    public static readonly StyledProperty<bool> IsWordEmphasisEnabledProperty =
        AvaloniaProperty.Register<WordLyricsPresenter, bool>(nameof(IsWordEmphasisEnabled), true);

    public static readonly StyledProperty<bool> IsWordEdgeFeatherEnabledProperty =
        AvaloniaProperty.Register<WordLyricsPresenter, bool>(nameof(IsWordEdgeFeatherEnabled), true);

    // 缓存当前排版结果，避免播放期间逐帧重复创建 FormattedText 和测量字符宽度。
    private LyricsLine? _cachedLine;
    private string? _cachedText;
    private double _cachedFontSize;
    private FontFamily _cachedFontFamily = Avalonia.Media.FontFamily.Default;
    private Typeface _typeface = new(Avalonia.Media.FontFamily.Default);
    private FormattedText? _fullText;

    // 下列数组的第一维都与 Line.Words 的索引一一对应：
    // wordStarts/wordWidths 是单词在整行中的水平范围；characterSegments 是字素簇的水平范围；
    // characterTimings 用于高亮和持续上浮，characterEmphasisTimings 用于 AMLL 波浪错相。
    private double[] _wordStarts = [];
    private double[] _wordWidths = [];
    private CharacterSegment[][] _characterSegments = [];
    private CharacterTiming[][] _characterTimings = [];
    private CharacterTiming[][] _characterEmphasisTimings = [];

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

    public bool IsWordEmphasisEnabled
    {
        get => GetValue(IsWordEmphasisEnabledProperty);
        set => SetValue(IsWordEmphasisEnabledProperty, value);
    }

    public bool IsWordEdgeFeatherEnabled
    {
        get => GetValue(IsWordEdgeFeatherEnabledProperty);
        set => SetValue(IsWordEdgeFeatherEnabledProperty, value);
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
            IsWordEmphasisEnabledProperty,
            IsWordEdgeFeatherEnabledProperty);
        AffectsMeasure<WordLyricsPresenter>(
            LineProperty,
            FontSizeProperty,
            FontFamilyProperty,
            ForegroundProperty,
            IsWordLiftEnabledProperty,
            IsWordEmphasisEnabledProperty);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        EnsureMetrics();
        if (_fullText == null)
        {
            return new Size(0, FontSize * 1.3);
        }

        // 缩放和左右位移会超出原始文本边界，测量时预留空间，避免强调峰值被父控件裁掉。
        var desiredWidth = _fullText.Width + GetWordEmphasisHorizontalSpace();
        var width = double.IsFinite(availableSize.Width) && availableSize.Width > 0
            ? Math.Min(desiredWidth, availableSize.Width)
            : desiredWidth;
        // 把上浮和缩放需要的纵向空间放在文字上方，因此基线需要相应下移。
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

        var mutedText = CreateFormatted(Line.Text ?? string.Empty, mutedBrush);
        var activeText = CreateFormatted(Line.Text ?? string.Empty, foreground);
        if (ShouldRenderAnimatedWords())
        {
            DrawAnimatedWords(context, mutedText, activeText, origin, Position);
            return;
        }

        // 先用灰色绘制整行歌词，再覆盖已播放部分
        context.DrawText(mutedText, origin);

        var filledWidth = ComputeFilledWidth(Position);
        if (filledWidth <= 0)
        {
            return;
        }

        var clipRect = new Rect(
            origin.X,
            origin.Y,
            filledWidth,
            Math.Max(activeText.Height, Bounds.Height));
        using (context.PushClip(clipRect))
        {
            context.DrawText(activeText, origin);
        }
    }

    private bool ShouldRenderAnimatedWords()
    {
        // 没有逐字动画时走整行绘制的快速路径。
        // 边缘羽化、上浮、强调任意启用时都走逐词/逐字符路径，保证高亮过渡不依赖强调条件。
        return Line is { Words.Count: > 0 } &&
               _wordStarts.Length == Line.Words.Count &&
               (IsWordEdgeFeatherEnabled ||
                IsWordLiftEnabled ||
                (IsWordEmphasisEnabled && HasEmphasizedWord()));
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
            // 同一个字符有两套时间轴：原始字符时间轴负责高亮/上浮，强调时间轴负责波浪错相。
            var characters = i < _characterSegments.Length ? _characterSegments[i] : [];
            var timings = i < _characterTimings.Length ? _characterTimings[i] : [];
            var emphasisTimings = i < _characterEmphasisTimings.Length ? _characterEmphasisTimings[i] : [];
            var isLastWord = i == words.Count - 1;
            var emphasizeCharacters = IsWordEmphasisEnabled && IsWordEmphasisEligible(words[i]);
            var emphasisAmount = emphasizeCharacters
                ? GetWordEmphasisAmount(words[i].StartTime, words[i].EndTime, isLastWord)
                : 0;
            if (characters.Length == 0 || timings.Length != characters.Length)
            {
                // 排版数据异常或无法拆字时退化为整个单词绘制，保证歌词仍然可见且动画不会抛异常。
                var emphasisTiming = GetCharacterEmphasisWindow(
                    words[i].StartTime,
                    words[i].EndTime,
                    0,
                    1,
                    isLastWord);
                DrawAnimatedSegment(
                    context,
                    mutedText,
                    activeText,
                    origin,
                    _wordStarts[i],
                    _wordWidths[i],
                    words[i].StartTime,
                    words[i].EndTime,
                    words[i].StartTime,
                    words[i].EndTime,
                    emphasisTiming.Start,
                    emphasisTiming.End,
                    emphasisAmount,
                    0,
                    1,
                    position);
                continue;
            }

            // 任一字符分到的时长过短时，逐字上浮会呈现为抖动，此时整词共享单词时间轴；
            // 强调效果仍按字符错相计算，不受这个回退影响。
            var useWholeWordLift = IsWordLiftEnabled && ShouldUseWholeWordLift(timings);
            for (var characterIndex = 0; characterIndex < characters.Length; characterIndex++)
            {
                var character = characters[characterIndex];
                var timing = timings[characterIndex];
                var emphasisTiming = characterIndex < emphasisTimings.Length
                    ? emphasisTimings[characterIndex]
                    : timing;
                DrawAnimatedSegment(
                    context,
                    mutedText,
                    activeText,
                    origin,
                    _wordStarts[i] + character.Start,
                    character.Width,
                    timing.Start,
                    timing.End,
                    useWholeWordLift ? words[i].StartTime : timing.Start,
                    useWholeWordLift ? words[i].EndTime : timing.End,
                    emphasisTiming.Start,
                    emphasisTiming.End,
                    emphasisAmount,
                    characterIndex,
                    characters.Length,
                    position);
            }
        }
    }

    private void DrawAnimatedSegment(
        DrawingContext context,
        FormattedText mutedText,
        FormattedText activeText,
        Point origin,
        double segmentStart,
        double segmentWidth,
        TimeSpan startTime,
        TimeSpan endTime,
        TimeSpan liftStartTime,
        TimeSpan liftEndTime,
        TimeSpan emphasisStartTime,
        TimeSpan emphasisEndTime,
        double emphasisAmount,
        int characterIndex,
        int characterCount,
        TimeSpan position)
    {
        // 高亮、上浮和强调互相独立：高亮可以结束，上浮会保持最终位置，强调则在自己的窗口内归零。
        var progress = GetProgress(position, startTime, endTime);
        var emphasisResponse = emphasisAmount > 0
            ? GetEmphasisWaveResponse(position, emphasisStartTime, emphasisEndTime)
            : 0;
        var liftOffset = IsWordLiftEnabled
            ? -GetWordLiftDistance() * GetCharacterLiftResponse(position, liftStartTime, liftEndTime)
            : 0;
        liftOffset += GetCharacterEmphasisVerticalOffset(
            FontSize,
            emphasisAmount,
            position,
            emphasisStartTime,
            emphasisEndTime);
        var horizontalOffset = GetCharacterEmphasisHorizontalOffset(
            FontSize,
            emphasisAmount,
            emphasisResponse,
            characterIndex,
            characterCount);
        // 暗色底字和高亮层稍后会在同一个 origin 下绘制，因此这里统一应用位移。
        var segmentOrigin = new Point(origin.X + horizontalOffset, origin.Y + liftOffset);
        var scale = GetCharacterEmphasisScale(
            position,
            emphasisStartTime,
            emphasisEndTime,
            emphasisAmount);
        DrawWordLayers(
            context,
            mutedText,
            activeText,
            segmentOrigin,
            segmentStart,
            segmentWidth,
            progress,
            scale);
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
            // 每个字符围绕自身中心缩放，而不是围绕整行中心缩放。
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
                if (IsWordEdgeFeatherEnabled)
                {
                    DrawFeatheredWord(context, activeText, origin, wordStart, wordWidth, progress);
                }
                else
                {
                    var filledClip = new Rect(
                        origin.X + wordStart,
                        0,
                        wordWidth * progress,
                        Bounds.Height);
                    using (context.PushClip(filledClip))
                    {
                        context.DrawText(activeText, origin);
                    }
                }
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

    private double GetMotionSpace()
    {
        // MeasureOverride 使用这里的最坏情况预留顶部空间，避免上浮、放大时被裁剪。
        double motionSpace = 0;
        if (IsWordLiftEnabled)
        {
            motionSpace += GetWordLiftDistance();
        }

        if (IsWordEmphasisEnabled && HasEmphasizedWord())
        {
            var maximumAmount = GetMaximumWordEmphasisAmount();
            motionSpace += ((_fullText?.Height ?? FontSize) * WordEmphasisScaleFactor * maximumAmount) +
                           (FontSize * ((WordEmphasisVerticalOffsetFactor * maximumAmount) +
                                        WordEmphasisFloatOffsetFactor));
        }

        return motionSpace > 0 ? motionSpace + GetBaselineOffset() : 0;
    }

    private double GetWordLiftDistance()
    {
        return Math.Clamp(FontSize * 0.07, 0.75, 1.15);
    }

    internal static double GetCharacterLiftResponse(TimeSpan position, TimeSpan start, TimeSpan end)
    {
        if (position <= start)
        {
            return 0;
        }

        if (end <= start || position >= end)
        {
            // 上浮是一次性的状态推进：到达顶部后保持 1，不再回落。
            return 1;
        }

        var progress = GetProgress(position, start, end);
        return 1 - Math.Pow(1 - progress, 3);
    }

    private static bool ShouldUseWholeWordLift(IReadOnlyList<CharacterTiming> timings)
    {
        // 只要一个字符的有效时间不足阈值，就统一退化为整词上浮，避免同一单词内混用两种节奏。
        foreach (var timing in timings)
        {
            if (IsCharacterLiftDurationTooShort(timing.Start, timing.End))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsCharacterLiftDurationTooShort(TimeSpan start, TimeSpan end)
    {
        return end <= start ||
               (end - start).TotalMilliseconds < MinimumCharacterLiftDurationMilliseconds;
    }

    private bool HasEmphasizedWord()
    {
        var words = Line?.Words;
        if (words == null)
        {
            return false;
        }

        foreach (var word in words)
        {
            if (IsWordEmphasisEligible(word))
            {
                return true;
            }
        }

        return false;
    }

    private double GetWordEmphasisHorizontalSpace()
    {
        if (!IsWordEmphasisEnabled || Line == null || _wordWidths.Length != Line.Words.Count)
        {
            return 0;
        }

        // 同时计算字符缩放产生的外扩和 AMLL 左右位移产生的外扩，取整行最大值。
        double maximumHorizontalSpace = 0;
        for (var i = 0; i < Line.Words.Count; i++)
        {
            if (!IsWordEmphasisEligible(Line.Words[i]))
            {
                continue;
            }

            var characters = i < _characterSegments.Length ? _characterSegments[i] : [];
            var amount = GetWordEmphasisAmount(
                Line.Words[i].StartTime,
                Line.Words[i].EndTime,
                i == Line.Words.Count - 1);
            if (characters.Length == 0)
            {
                var scaleSpace = _wordWidths[i] * WordEmphasisScaleFactor * amount;
                var offsetSpace = FontSize * WordEmphasisHorizontalOffsetFactor * amount;
                maximumHorizontalSpace = Math.Max(maximumHorizontalSpace, scaleSpace + offsetSpace);
                continue;
            }

            for (var characterIndex = 0; characterIndex < characters.Length; characterIndex++)
            {
                var scaleSpace = characters[characterIndex].Width * WordEmphasisScaleFactor * amount;
                var offset = Math.Abs(GetCharacterEmphasisHorizontalOffset(
                    FontSize,
                    amount,
                    1,
                    characterIndex,
                    characters.Length));
                maximumHorizontalSpace = Math.Max(maximumHorizontalSpace, scaleSpace + (offset * 2));
            }
        }

        return maximumHorizontalSpace;
    }

    internal static bool IsWordEmphasisEligible(LyricsWord word)
    {
        // 单词时间恰好 1 秒不触发。
        return (word.EndTime - word.StartTime).TotalMilliseconds > WordEmphasisMinimumDurationMilliseconds;
    }

    internal static double GetWordEmphasisAmount(
        TimeSpan start,
        TimeSpan end,
        bool isLastWord)
    {
        // AMLL 的强度曲线：以 2 秒为分界，短词使用三次方压低强度，长词使用平方根平缓增长；
        // 然后乘基础系数和末词增益，最终限制在 1.2，最大缩放即 1 + 0.1 * 1.2 = 1.12。
        var durationMilliseconds = Math.Max(
            WordEmphasisMinimumDurationMilliseconds,
            (end - start).TotalMilliseconds);
        var amount = durationMilliseconds / 2000;
        amount = amount > 1 ? Math.Sqrt(amount) : Math.Pow(amount, 3);
        amount *= WordEmphasisAmountFactor;
        if (isLastWord)
        {
            amount *= WordEmphasisLastWordAmountFactor;
        }

        return Math.Min(WordEmphasisMaximumAmount, amount);
    }

    internal static double GetCharacterEmphasisScale(
        TimeSpan position,
        TimeSpan start,
        TimeSpan end,
        double amount)
    {
        return 1 + (WordEmphasisScaleFactor * amount * GetEmphasisWaveResponse(position, start, end));
    }

    internal static double GetCharacterEmphasisHorizontalOffset(
        double fontSize,
        double amount,
        double response,
        int characterIndex,
        int characterCount)
    {
        // 字符按索引向两侧轻微展开。每个字符的窗口又有错相，所以展开会沿文字方向形成波浪。
        var anchorCharacterCount = Math.Max(1, characterCount);
        return -response * WordEmphasisHorizontalOffsetFactor * amount * fontSize *
               ((anchorCharacterCount / 2.0) - characterIndex);
    }

    internal static double GetCharacterEmphasisVerticalOffset(
        double fontSize,
        double amount,
        TimeSpan position,
        TimeSpan start,
        TimeSpan end)
    {
        if (amount <= 0 || end <= start || position <= start || position >= end)
        {
            return 0;
        }

        // 第一项跟随缩放曲线，第二项参考 AMLL 的正弦漂浮；两者共用同一窗口，
        // 确保缩放结束时额外位移也回到 0，只保留此前已经完成的持续上浮位置。
        var progress = GetProgress(position, start, end);
        var scaleResponse = GetEmphasisWaveResponse(progress);
        var floatResponse = Math.Sin(Math.PI * progress);
        return -fontSize * ((WordEmphasisVerticalOffsetFactor * amount * scaleResponse) +
                            (WordEmphasisFloatOffsetFactor * floatResponse));
    }

    internal static double GetEmphasisWaveResponse(TimeSpan position, TimeSpan start, TimeSpan end)
    {
        if (end <= start || position <= start || position >= end)
        {
            return 0;
        }

        return GetEmphasisWaveResponse(GetProgress(position, start, end));
    }

    internal static double GetEmphasisWaveResponse(double progress)
    {
        progress = Math.Clamp(progress, 0, 1);
        if (progress <= 0 || progress >= 1)
        {
            return 0;
        }

        // AMLL 使用两段不同的贝塞尔曲线：前半段快速升至峰值，后半段更从容地回到原始大小。
        return progress < 0.5
            ? EvaluateCubicBezier(progress / 0.5, 0.2, 0.4, 0.58, 1)
            : 1 - EvaluateCubicBezier((progress - 0.5) / 0.5, 0.3, 0, 0.58, 1);
    }

    internal static (TimeSpan Start, TimeSpan End) GetCharacterEmphasisWindow(
        TimeSpan wordStart,
        TimeSpan wordEnd,
        int characterIndex,
        int characterCount,
        bool isLastWord)
    {
        if (wordEnd <= wordStart)
        {
            return (wordStart, wordEnd);
        }

        var durationMilliseconds = Math.Max(
            WordEmphasisMinimumDurationMilliseconds,
            (wordEnd - wordStart).TotalMilliseconds);
        if (isLastWord)
        {
            durationMilliseconds *= WordEmphasisLastWordDurationFactor;
        }

        // AMLL 的字符延迟为 duration / 2.5 / 字符数 * 字符索引。
        // 每个字符的动画时长仍是完整单词时长，因此相邻字符窗口大量重叠，形成连续波浪，
        // 而不是等前一个字符完整缩放完再启动下一个字符。
        var anchorCharacterCount = Math.Max(1, characterCount);
        var normalizedCharacterIndex = Math.Clamp(characterIndex, 0, anchorCharacterCount - 1);
        var delayMilliseconds = durationMilliseconds / WordEmphasisCharacterStaggerDivisor /
                                anchorCharacterCount * normalizedCharacterIndex;
        var start = wordStart + TimeSpan.FromMilliseconds(delayMilliseconds);
        return (start, start + TimeSpan.FromMilliseconds(durationMilliseconds));
    }

    private double GetMaximumWordEmphasisAmount()
    {
        var words = Line?.Words;
        if (words == null)
        {
            return 0;
        }

        double maximumAmount = 0;
        for (var i = 0; i < words.Count; i++)
        {
            if (!IsWordEmphasisEligible(words[i]))
            {
                continue;
            }

            maximumAmount = Math.Max(
                maximumAmount,
                GetWordEmphasisAmount(words[i].StartTime, words[i].EndTime, i == words.Count - 1));
        }

        return maximumAmount;
    }

    private static double EvaluateCubicBezier(
        double x,
        double controlX1,
        double controlY1,
        double controlX2,
        double controlY2)
    {
        // 贝塞尔控制点给出的是参数 t 对应的 (x, y)。调用方传入的是时间进度 x，
        // 因此先用二分法反求 t，再取同一 t 上的 y，行为才与 CSS/bezier-easing 一致。
        x = Math.Clamp(x, 0, 1);
        double lower = 0;
        double upper = 1;
        double parameter = x;
        for (var i = 0; i < 20; i++)
        {
            var currentX = SampleCubicBezier(parameter, controlX1, controlX2);
            if (Math.Abs(currentX - x) < 0.000001)
            {
                break;
            }

            if (currentX < x)
            {
                lower = parameter;
            }
            else
            {
                upper = parameter;
            }

            parameter = (lower + upper) / 2;
        }

        return SampleCubicBezier(parameter, controlY1, controlY2);
    }

    private static double SampleCubicBezier(double parameter, double control1, double control2)
    {
        var inverse = 1 - parameter;
        return (3 * inverse * inverse * parameter * control1) +
               (3 * inverse * parameter * parameter * control2) +
               (parameter * parameter * parameter);
    }

    private static CharacterTiming GetCharacterTiming(
        LyricsWord word,
        CharacterSegment character,
        double wordWidth)
    {
        if (wordWidth <= 0 || word.EndTime <= word.StartTime)
        {
            return new CharacterTiming(word.StartTime, word.EndTime);
        }

        // 歌词数据只提供单词起止时间，没有逐字符时间；按字符的实际渲染宽度占比分配时间，
        // 使宽字符获得更长的高亮/上浮时间，窄字符不会拖慢整词推进。
        var startRatio = Math.Clamp(character.Start / wordWidth, 0, 1);
        var endRatio = Math.Clamp((character.Start + character.Width) / wordWidth, startRatio, 1);
        var durationTicks = (word.EndTime - word.StartTime).Ticks;
        var startOffset = (long)Math.Round(durationTicks * startRatio);
        var endOffset = (long)Math.Round(durationTicks * endRatio);
        return new CharacterTiming(
            word.StartTime + TimeSpan.FromTicks(startOffset),
            word.StartTime + TimeSpan.FromTicks(endOffset));
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
            ReferenceEquals(_cachedLine, line) &&
            string.Equals(_cachedText, text, StringComparison.Ordinal) &&
            Math.Abs(_cachedFontSize - FontSize) < 0.01 &&
            Equals(_cachedFontFamily, FontFamily))
        {
            return;
        }

        // 只有歌词对象、文本或字体变化时才重建排版缓存；Position 的逐帧变化只触发 Render。
        _cachedLine = line;
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
        // 第一轮确定每个单词在整行中的位置，第二轮再拆分字素簇并生成两套字符时间轴。
        if (line == null || line.Words.Count == 0)
        {
            _wordStarts = [];
            _wordWidths = [];
            _characterSegments = [];
            _characterTimings = [];
            _characterEmphasisTimings = [];
            return;
        }

        _wordStarts = new double[line.Words.Count];
        _wordWidths = new double[line.Words.Count];
        _characterSegments = new CharacterSegment[line.Words.Count][];
        _characterTimings = new CharacterTiming[line.Words.Count][];
        _characterEmphasisTimings = new CharacterTiming[line.Words.Count][];
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

        for (var i = 0; i < line.Words.Count; i++)
        {
            // 字符段决定裁剪区域；字符时间按宽度分配；强调时间只按字符索引错相。
            _characterSegments[i] = BuildCharacterSegments(
                line.Words[i].Text ?? string.Empty,
                brush,
                _wordWidths[i]);
            _characterTimings[i] = _characterSegments[i]
                .Select(character => GetCharacterTiming(line.Words[i], character, _wordWidths[i]))
                .ToArray();
            _characterEmphasisTimings[i] = _characterTimings[i]
                .Select((_, characterIndex) =>
                {
                    var window = GetCharacterEmphasisWindow(
                        line.Words[i].StartTime,
                        line.Words[i].EndTime,
                        characterIndex,
                        _characterSegments[i].Length,
                        i == line.Words.Count - 1);
                    return new CharacterTiming(window.Start, window.End);
                })
                .ToArray();
        }
    }

    private CharacterSegment[] BuildCharacterSegments(string text, IBrush brush, double targetWidth)
    {
        // 这里按 Unicode 字素簇拆分，而不是按 UTF-16 char 拆分，避免拆开 emoji、组合音标等字符。
        var textElements = SplitTextElements(text);
        if (textElements.Length == 0 || targetWidth <= 0)
        {
            return [];
        }

        var segments = new List<CharacterSegment>(textElements.Length);
        double cursor = 0;
        double leadingWhitespaceWidth = 0;
        foreach (var textElement in textElements)
        {
            var layout = CreateFormatted(textElement, brush);
            var width = layout.WidthIncludingTrailingWhitespace;
            if (width <= 0)
            {
                width = layout.Width;
            }

            if (string.IsNullOrWhiteSpace(textElement))
            {
                // 空白不单独做动画：词尾空白并入前一字符，词首空白并入后一字符，保证整体宽度连续。
                if (segments.Count > 0)
                {
                    var previous = segments[^1];
                    segments[^1] = previous with { Width = previous.Width + width };
                }
                else
                {
                    leadingWhitespaceWidth += width;
                }
            }
            else
            {
                segments.Add(new CharacterSegment(
                    Math.Max(0, cursor - leadingWhitespaceWidth),
                    leadingWhitespaceWidth + width));
                leadingWhitespaceWidth = 0;
            }

            cursor += width;
        }

        if (segments.Count == 0)
        {
            return [new CharacterSegment(0, targetWidth)];
        }

        if (cursor <= 0)
        {
            var uniformWidth = targetWidth / segments.Count;
            return segments
                .Select((_, index) => new CharacterSegment(index * uniformWidth, uniformWidth))
                .ToArray();
        }

        // 单字符测量之和可能因字距/kerning 与整词测量不同，统一缩放回单词的真实目标宽度。
        var scale = targetWidth / cursor;
        return segments
            .Select(segment => new CharacterSegment(segment.Start * scale, segment.Width * scale))
            .ToArray();
    }

    internal static string[] SplitTextElements(string text)
    {
        // ParseCombiningCharacters 返回每个 Unicode 字素簇的 UTF-16 起始位置。
        var normalizedText = text ?? string.Empty;
        var starts = StringInfo.ParseCombiningCharacters(normalizedText);
        var elements = new string[starts.Length];
        for (var i = 0; i < starts.Length; i++)
        {
            var end = i + 1 < starts.Length ? starts[i + 1] : normalizedText.Length;
            elements[i] = normalizedText[starts[i]..end];
        }

        return elements;
    }

    private FormattedText CreateFormatted(string text, IBrush brush)
    {
        return new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
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

    // 单个字素簇在所属单词内的水平起点和宽度，用于裁剪暗色/高亮文本。
    private readonly record struct CharacterSegment(double Start, double Width);

    // 单个动画窗口的绝对播放时间；高亮/上浮与强调各自维护一套窗口。
    private readonly record struct CharacterTiming(TimeSpan Start, TimeSpan End);
}
