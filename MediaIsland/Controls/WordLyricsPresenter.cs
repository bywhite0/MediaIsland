using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MediaIsland.Services.Lyrics.Models;

namespace MediaIsland.Controls;

/// <summary>
/// 自定义的单行逐字歌词渲染器。
/// </summary>
/// <remarks>
/// 处理流程分为三步：先在 <see cref="EnsureMetrics"/> 中测量整行、单词和字素簇；
/// 再把单词有效时长按字符实际宽度分配给各字符；最后在渲染阶段分别计算高亮（可选边缘羽化）、持续上浮和 AMLL 强调效果（含辉光），
/// 让暗色底字与高亮文字共用同一套位移和缩放，以免动画后出现重影。
/// </remarks>
public sealed class WordLyricsPresenter : Control
{
    // AMLL 强调参数：单词时长决定强调强度，强度再控制缩放与位移幅度
    // 末词仍按 AMLL 逻辑获得更强、更长的强调，但所有超过阈值的单词都可以触发效果
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
    // 强调辉光：对齐 AMLL text-shadow —— 时长决定 blur，半径用 blur，
    // 基础不透明度 empEasing(response) * blur，再乘视觉增益（离屏模糊后原值偏弱）。
    // 实现：单字离屏位图 + CPU 高斯模糊；绘制时裁到本行内。
    private const double WordEmphasisBlurDurationDivisor = 3000;
    private const double WordEmphasisBlurFactor = 0.5;
    private const double WordEmphasisLastWordBlurFactor = 1.5;
    private const double WordEmphasisMaximumBlur = 0.8;
    private const double WordEmphasisGlowRadiusEmCap = 0.3;
    private const double WordEmphasisGlowRadiusBlurFactor = 0.3;
    // 视觉增益略抬一点即可；过高会发灰、发脏，并放大离屏图黑底瑕疵
    private const double WordEmphasisGlowOpacityBoost = 1.7;
    private const int WordEmphasisGlowMaxBlurRadiusPixels = 12;
    private const int WordEmphasisGlowSampleCount = 8;
    private const double MinimumCharacterLiftDurationMilliseconds = 120;
    // AMLL 背景行目标不透明度；作用在画刷上，使已完成单词保持约 0.4 的亮度
    private const double BackgroundLineActiveOpacity = 0.4;
    private const double BackgroundLineMutedOpacity = 0.28;

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

    public static readonly StyledProperty<bool> IsWordEmphasisGlowEnabledProperty =
        AvaloniaProperty.Register<WordLyricsPresenter, bool>(nameof(IsWordEmphasisGlowEnabled), true);

    public static readonly StyledProperty<bool> IsWordEdgeFeatherEnabledProperty =
        AvaloniaProperty.Register<WordLyricsPresenter, bool>(nameof(IsWordEdgeFeatherEnabled), true);

    public static readonly StyledProperty<bool> IsBackgroundLineProperty =
        AvaloniaProperty.Register<WordLyricsPresenter, bool>(nameof(IsBackgroundLine));

    // 缓存当前排版结果，避免播放期间逐帧重复创建格式化文本并测量字符宽度
    private LyricsLine? _cachedLine;
    private string? _cachedText;
    private double _cachedFontSize;
    private FontFamily _cachedFontFamily = Avalonia.Media.FontFamily.Default;
    private Typeface _typeface = new(Avalonia.Media.FontFamily.Default);
    private FormattedText? _fullText;

    // 下列数组的第一维都与当前行单词索引一一对应：
    // 单词起点/宽度是单词在整行中的水平范围；字素段是字素簇的水平范围；
    // 字符时间轴用于高亮和持续上浮，强调时间轴用于 AMLL 波浪错相
    private double[] _wordStarts = [];
    private double[] _wordWidths = [];
    private CharacterSegment[][] _characterSegments = [];
    private CharacterTiming[][] _characterTimings = [];
    private CharacterTiming[][] _characterEmphasisTimings = [];
    // 模糊辉光位图缓存：按字素/字号/半径复用，避免逐帧重渲染与重模糊
    private readonly Dictionary<string, GlowBitmapCacheEntry> _glowBitmapCache = new();

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

    public bool IsWordEmphasisGlowEnabled
    {
        get => GetValue(IsWordEmphasisGlowEnabledProperty);
        set => SetValue(IsWordEmphasisGlowEnabledProperty, value);
    }

    public bool IsWordEdgeFeatherEnabled
    {
        get => GetValue(IsWordEdgeFeatherEnabledProperty);
        set => SetValue(IsWordEdgeFeatherEnabledProperty, value);
    }

    public bool IsBackgroundLine
    {
        get => GetValue(IsBackgroundLineProperty);
        set => SetValue(IsBackgroundLineProperty, value);
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
            IsWordEmphasisGlowEnabledProperty,
            IsWordEdgeFeatherEnabledProperty,
            IsBackgroundLineProperty);
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

        // 缩放和左右位移会超出原始文本边界，测量时预留空间，避免强调峰值被父控件裁掉
        var desiredWidth = _fullText.Width + GetWordEmphasisHorizontalSpace();
        var width = double.IsFinite(availableSize.Width) && availableSize.Width > 0
            ? Math.Min(desiredWidth, availableSize.Width)
            : desiredWidth;
        // 把上浮和缩放需要的纵向空间放在文字上方，因此基线需要相应下移
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
        var mutedBrush = IsBackgroundLine
            ? CreateOpacityBrush(foreground, BackgroundLineMutedOpacity)
            : CreateMutedBrush(foreground);
        var activeBrush = IsBackgroundLine
            ? CreateOpacityBrush(foreground, BackgroundLineActiveOpacity)
            : foreground;
        var motionSpace = GetMotionSpace();
        var origin = new Point(GetHorizontalOrigin(_fullText.Width),
            Math.Max(0, (Bounds.Height - _fullText.Height + motionSpace) / 2));

        var mutedText = CreateFormatted(Line.Text ?? string.Empty, mutedBrush);
        var activeText = CreateFormatted(Line.Text ?? string.Empty, activeBrush);
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
        // 没有逐字动画时走整行绘制
        // 边缘羽化、上浮、强调任意启用时都走逐词/逐字符路径
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
            // 同一个字符有两套时间轴：原始字符时间轴负责高亮/上浮，强调时间轴负责波浪错相
            var characters = i < _characterSegments.Length ? _characterSegments[i] : [];
            var timings = i < _characterTimings.Length ? _characterTimings[i] : [];
            var emphasisTimings = i < _characterEmphasisTimings.Length ? _characterEmphasisTimings[i] : [];
            var isLastWord = i == words.Count - 1;
            var emphasizeCharacters = IsWordEmphasisEnabled && IsWordEmphasisEligible(words[i]);
            var emphasisAmount = emphasizeCharacters
                ? GetWordEmphasisAmount(words[i].StartTime, words[i].EndTime, isLastWord)
                : 0;
            var emphasisBlur = emphasizeCharacters && IsWordEmphasisGlowEnabled
                ? GetWordEmphasisBlur(words[i].StartTime, words[i].EndTime, isLastWord)
                : 0;
            if (characters.Length == 0 || timings.Length != characters.Length)
            {
                // 排版数据异常或无法拆字时退化为整个单词绘制，保证歌词仍然可见且动画不会抛异常
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
                    words[i].Text ?? string.Empty,
                    words[i].StartTime,
                    words[i].EndTime,
                    words[i].StartTime,
                    words[i].EndTime,
                    emphasisTiming.Start,
                    emphasisTiming.End,
                    emphasisAmount,
                    emphasisBlur,
                    0,
                    1,
                    position);
                continue;
            }

            // 任一字符分到的时长过短时，逐字上浮会呈现为抖动，此时整词共享单词时间轴；
            // 强调效果仍按字符错相计算，不受这个回退影响
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
                    character.Text,
                    timing.Start,
                    timing.End,
                    useWholeWordLift ? words[i].StartTime : timing.Start,
                    useWholeWordLift ? words[i].EndTime : timing.End,
                    emphasisTiming.Start,
                    emphasisTiming.End,
                    emphasisAmount,
                    emphasisBlur,
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
        string glowGrapheme,
        TimeSpan startTime,
        TimeSpan endTime,
        TimeSpan liftStartTime,
        TimeSpan liftEndTime,
        TimeSpan emphasisStartTime,
        TimeSpan emphasisEndTime,
        double emphasisAmount,
        double emphasisBlur,
        int characterIndex,
        int characterCount,
        TimeSpan position)
    {
        // 高亮、上浮和强调互相独立：高亮可以结束，上浮会保持最终位置，强调则在自己的窗口内归零
        var progress = GetProgress(position, startTime, endTime);
        var emphasisResponse = emphasisAmount > 0 || emphasisBlur > 0
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
        // 暗色底字和高亮层稍后会在同一个 origin 下绘制，因此这里统一应用位移
        var segmentOrigin = new Point(origin.X + horizontalOffset, origin.Y + liftOffset);
        var scale = GetCharacterEmphasisScale(
            position,
            emphasisStartTime,
            emphasisEndTime,
            emphasisAmount);
        // AMLL：rgba 不透明度 = empEasing(x) * blur；半径仅依赖 blur。
        // 辉光可单独关闭，不影响缩放与位移强调。
        var glowLevel = IsWordEmphasisGlowEnabled
            ? GetEmphasisGlowLevel(emphasisBlur, emphasisResponse)
            : 0;
        if (IsBackgroundLine && glowLevel > 0)
        {
            glowLevel *= BackgroundLineActiveOpacity;
        }

        DrawWordLayers(
            context,
            mutedText,
            activeText,
            segmentOrigin,
            segmentStart,
            segmentWidth,
            glowGrapheme,
            progress,
            scale,
            emphasisBlur,
            glowLevel);
    }

    private void DrawWordLayers(
        DrawingContext context,
        FormattedText mutedText,
        FormattedText activeText,
        Point origin,
        double wordStart,
        double wordWidth,
        string glowGrapheme,
        double progress,
        double scale,
        double emphasisBlur,
        double glowLevel)
    {
        if (scale > 1.0001)
        {
            // 每个字符围绕自身中心缩放，而不是围绕整行中心缩放
            var center = new Point(
                origin.X + wordStart + (wordWidth / 2),
                origin.Y + (_fullText!.Height / 2));
            using (context.PushTransform(CreateScaleAround(scale, center)))
            {
                DrawWordLayersCore(
                    context,
                    mutedText,
                    activeText,
                    origin,
                    wordStart,
                    wordWidth,
                    glowGrapheme,
                    progress,
                    emphasisBlur,
                    glowLevel);
            }

            return;
        }

        DrawWordLayersCore(
            context,
            mutedText,
            activeText,
            origin,
            wordStart,
            wordWidth,
            glowGrapheme,
            progress,
            emphasisBlur,
            glowLevel);
    }

    private void DrawWordLayersCore(
        DrawingContext context,
        FormattedText mutedText,
        FormattedText activeText,
        Point origin,
        double wordStart,
        double wordWidth,
        string glowGrapheme,
        double progress,
        double emphasisBlur,
        double glowLevel)
    {
        var wordClip = new Rect(origin.X + wordStart, 0, wordWidth, Bounds.Height);
        using (context.PushClip(wordClip))
        {
            // 暗色底字与高亮层共用同一位移和缩放，避免上浮后残留重影
            context.DrawText(mutedText, origin);
        }

        // 单字离屏辉光可外扩柔边，且不会点亮邻字
        if (glowLevel > 0.001 && !string.IsNullOrEmpty(glowGrapheme))
        {
            DrawEmphasisGlow(
                context,
                origin,
                wordStart,
                wordWidth,
                glowGrapheme,
                emphasisBlur,
                glowLevel);
        }

        using (context.PushClip(wordClip))
        {
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

    private void DrawEmphasisGlow(
        DrawingContext context,
        Point origin,
        double wordStart,
        double wordWidth,
        string glowGrapheme,
        double emphasisBlur,
        double glowLevel)
    {
        if (emphasisBlur <= 0 || wordWidth <= 0 || glowLevel <= 0.001)
        {
            return;
        }

        var radius = GetEmphasisGlowRadius(FontSize, emphasisBlur);
        if (radius <= 0.01)
        {
            return;
        }

        // AMLL: text-shadow 0 0 min(0.3, blur*0.3)em rgba(255,255,255, empEasing*blur)
        // 原始 alpha 经高斯后会被稀释；这里做视觉增益，上限 1。
        var opacity = Math.Clamp(glowLevel * WordEmphasisGlowOpacityBoost, 0, 1);
        if (!TryGetGlowBitmap(glowGrapheme, radius, out var glowBitmap, out var pad))
        {
            // 离屏渲染不可用时退回环状采样，仍使用单字文本避免邻字泄漏。
            DrawEmphasisGlowFallback(
                context,
                origin,
                wordStart,
                wordWidth,
                glowGrapheme,
                radius,
                opacity);
            return;
        }

        var dest = new Rect(
            origin.X + wordStart - pad,
            origin.Y - pad,
            glowBitmap.PixelSize.Width,
            glowBitmap.PixelSize.Height);
        // 裁到本行控件内，避免模糊柔边画进相邻歌词行。
        using (context.PushClip(new Rect(Bounds.Size)))
        using (context.PushOpacity(opacity))
        {
            context.DrawImage(glowBitmap, dest);
        }
    }

    private void DrawEmphasisGlowFallback(
        DrawingContext context,
        Point origin,
        double wordStart,
        double wordWidth,
        string glowGrapheme,
        double radius,
        double opacity)
    {
        var glowChar = CreateFormatted(glowGrapheme, Brushes.White);
        var glowOrigin = new Point(origin.X + wordStart, origin.Y);
        var glyphWidth = Math.Max(wordWidth, glowChar.Width);
        var glyphHeight = Math.Max(_fullText?.Height ?? FontSize * 1.2, glowChar.Height);
        var glowClip = new Rect(
            glowOrigin.X - radius,
            Math.Min(0, glowOrigin.Y - radius),
            glyphWidth + (radius * 2),
            Math.Max(Bounds.Height, glyphHeight + (radius * 2)));
        if (glowClip.Width <= 0 || glowClip.Height <= 0)
        {
            return;
        }

        var lineClip = new Rect(Bounds.Size);
        var clipped = glowClip.Intersect(lineClip);
        if (clipped.Width <= 0 || clipped.Height <= 0)
        {
            return;
        }

        using (context.PushClip(clipped))
        using (context.PushOpacity(opacity))
        {
            using (context.PushOpacity(0.35))
            {
                context.DrawText(glowChar, glowOrigin);
            }

            var sampleOpacity = 0.55 / WordEmphasisGlowSampleCount;
            using (context.PushOpacity(sampleOpacity))
            {
                for (var i = 0; i < WordEmphasisGlowSampleCount; i++)
                {
                    var angle = Math.PI * 2 * i / WordEmphasisGlowSampleCount;
                    var sampleOrigin = new Point(
                        glowOrigin.X + (Math.Cos(angle) * radius),
                        glowOrigin.Y + (Math.Sin(angle) * radius));
                    context.DrawText(glowChar, sampleOrigin);
                }
            }
        }
    }

    private bool TryGetGlowBitmap(
        string glowGrapheme,
        double radius,
        out Bitmap bitmap,
        out double pad)
    {
        bitmap = null!;
        pad = 0;
        var radiusPx = Math.Clamp(
            (int)Math.Ceiling(radius),
            1,
            WordEmphasisGlowMaxBlurRadiusPixels);
        // 高斯核半宽约等于模糊半径，外扩多留 1 像素即可；过大会撑高离屏图并诱发多行溢出
        pad = radiusPx + 1;

        var glowChar = CreateFormatted(glowGrapheme, Brushes.White);
        var glyphWidth = Math.Max(1, (int)Math.Ceiling(Math.Max(glowChar.Width, 1)));
        var glyphHeight = Math.Max(
            1,
            (int)Math.Ceiling(Math.Max(glowChar.Height, FontSize * 1.2)));
        var pixelWidth = glyphWidth + ((int)pad * 2);
        var pixelHeight = glyphHeight + ((int)pad * 2);
        if (pixelWidth <= 0 || pixelHeight <= 0 || pixelWidth > 1024 || pixelHeight > 1024)
        {
            return false;
        }

        var cacheKey =
            glowGrapheme + "\u001f" +
            FontSize.ToString("0.##", CultureInfo.InvariantCulture) + "\u001f" +
            radiusPx.ToString(CultureInfo.InvariantCulture) + "\u001f" +
            pixelWidth.ToString(CultureInfo.InvariantCulture) + "x" +
            pixelHeight.ToString(CultureInfo.InvariantCulture) + "\u001f" +
            (FontFamily?.ToString() ?? string.Empty);
        if (_glowBitmapCache.TryGetValue(cacheKey, out var cached))
        {
            bitmap = cached.Bitmap;
            pad = cached.Pad;
            return true;
        }

        try
        {
            var dpi = new Vector(96, 96);
            var pixelSize = new PixelSize(pixelWidth, pixelHeight);
            using var renderTarget = new RenderTargetBitmap(pixelSize, dpi);
            using (var drawingContext = renderTarget.CreateDrawingContext(true))
            {
                drawingContext.DrawText(glowChar, new Point(pad, pad));
            }

            var stride = pixelWidth * 4;
            var sourcePixels = new byte[stride * pixelHeight];
            var handle = GCHandle.Alloc(sourcePixels, GCHandleType.Pinned);
            try
            {
                renderTarget.CopyPixels(
                    new PixelRect(0, 0, pixelWidth, pixelHeight),
                    handle.AddrOfPinnedObject(),
                    sourcePixels.Length,
                    stride);
            }
            finally
            {
                handle.Free();
            }

            // 离屏位图清屏常为不透明黑，先清成透明再模糊，避免黑晕
            SanitizeGlowSourcePixels(sourcePixels);
            // 核半径略收，更接近文字阴影的观感，避免糊成一片
            var blurRadiusPx = Math.Max(1, (int)Math.Round(radiusPx * 0.7));
            var blurredPixels = BlurBgra8888(sourcePixels, pixelWidth, pixelHeight, blurRadiusPx);
            ForcePremultipliedWhiteGlow(blurredPixels);
            var writeable = new WriteableBitmap(
                pixelSize,
                dpi,
                PixelFormat.Bgra8888,
                AlphaFormat.Premul);
            using (var framebuffer = writeable.Lock())
            {
                Marshal.Copy(blurredPixels, 0, framebuffer.Address, blurredPixels.Length);
            }

            _glowBitmapCache[cacheKey] = new GlowBitmapCacheEntry(writeable, pad);
            bitmap = writeable;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 可分离高斯模糊（先水平后垂直），对预乘 BGRA 混合，适合文字透明通道辉光。
    /// </summary>
    internal static byte[] BlurBgra8888(byte[] source, int width, int height, int radius)
    {
        if (source.Length < width * height * 4 || width <= 0 || height <= 0 || radius <= 0)
        {
            return source;
        }

        radius = Math.Clamp(radius, 1, WordEmphasisGlowMaxBlurRadiusPixels);
        var kernel = CreateGaussianKernel(radius);
        var temp = new byte[source.Length];
        var dest = new byte[source.Length];
        ConvolveBgraHorizontal(source, temp, width, height, kernel);
        ConvolveBgraVertical(temp, dest, width, height, kernel);
        return dest;
    }

    private static float[] CreateGaussianKernel(int radius)
    {
        // 略收高斯标准差，减少糊成一团
        var sigma = Math.Max(0.4, radius / 2.6);
        var twoSigmaSquare = 2 * sigma * sigma;
        var size = radius * 2 + 1;
        var kernel = new float[size];
        float sum = 0;
        for (var i = 0; i < size; i++)
        {
            var x = i - radius;
            var value = (float)Math.Exp(-(x * x) / twoSigmaSquare);
            kernel[i] = value;
            sum += value;
        }

        for (var i = 0; i < size; i++)
        {
            kernel[i] /= sum;
        }

        return kernel;
    }

    private static void ConvolveBgraHorizontal(
        byte[] source,
        byte[] dest,
        int width,
        int height,
        float[] kernel)
    {
        var radius = kernel.Length / 2;
        for (var y = 0; y < height; y++)
        {
            var row = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                float b = 0, g = 0, r = 0, a = 0;
                for (var k = 0; k < kernel.Length; k++)
                {
                    var sampleX = Math.Clamp(x + k - radius, 0, width - 1);
                    var index = row + (sampleX * 4);
                    var weight = kernel[k];
                    // 源为非预乘白字，先按透明通道预乘再模糊，避免暗边
                    var alpha = source[index + 3] / 255f;
                    b += source[index + 0] * alpha * weight;
                    g += source[index + 1] * alpha * weight;
                    r += source[index + 2] * alpha * weight;
                    a += source[index + 3] * weight;
                }

                var destIndex = row + (x * 4);
                var outAlpha = Math.Clamp(a, 0, 255);
                if (outAlpha <= 0.5f)
                {
                    dest[destIndex + 0] = 0;
                    dest[destIndex + 1] = 0;
                    dest[destIndex + 2] = 0;
                    dest[destIndex + 3] = 0;
                    continue;
                }

                // 输出预乘 BGRA，供预乘格式的可写位图使用
                dest[destIndex + 0] = (byte)Math.Clamp(b, 0, 255);
                dest[destIndex + 1] = (byte)Math.Clamp(g, 0, 255);
                dest[destIndex + 2] = (byte)Math.Clamp(r, 0, 255);
                dest[destIndex + 3] = (byte)Math.Clamp(outAlpha, 0, 255);
            }
        }
    }

    private static void ConvolveBgraVertical(
        byte[] source,
        byte[] dest,
        int width,
        int height,
        float[] kernel)
    {
        var radius = kernel.Length / 2;
        var stride = width * 4;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                float b = 0, g = 0, r = 0, a = 0;
                var column = x * 4;
                for (var k = 0; k < kernel.Length; k++)
                {
                    var sampleY = Math.Clamp(y + k - radius, 0, height - 1);
                    var index = (sampleY * stride) + column;
                    var weight = kernel[k];
                    // 输入已经是预乘颜色
                    b += source[index + 0] * weight;
                    g += source[index + 1] * weight;
                    r += source[index + 2] * weight;
                    a += source[index + 3] * weight;
                }

                var destIndex = (y * stride) + column;
                dest[destIndex + 0] = (byte)Math.Clamp(b, 0, 255);
                dest[destIndex + 1] = (byte)Math.Clamp(g, 0, 255);
                dest[destIndex + 2] = (byte)Math.Clamp(r, 0, 255);
                dest[destIndex + 3] = (byte)Math.Clamp(a, 0, 255);
            }
        }
    }

    /// <summary>
    /// 离屏渲染目标清屏经常是不透明黑；把近似黑底清成全透明，只保留字形覆盖。
    /// </summary>
    private static void SanitizeGlowSourcePixels(byte[] bgra)
    {
        for (var i = 0; i < bgra.Length; i += 4)
        {
            var b = bgra[i + 0];
            var g = bgra[i + 1];
            var r = bgra[i + 2];
            var a = bgra[i + 3];
            // 纯黑或近黑像素一律视为背景（不论透明通道取值）
            if (b <= 8 && g <= 8 && r <= 8)
            {
                bgra[i + 0] = 0;
                bgra[i + 1] = 0;
                bgra[i + 2] = 0;
                bgra[i + 3] = 0;
                continue;
            }

            // 强制白色覆盖：用亮度近似字形覆盖率，避免彩色或灰边
            var coverage = Math.Max(a, Math.Max(b, Math.Max(g, r)));
            bgra[i + 0] = coverage;
            bgra[i + 1] = coverage;
            bgra[i + 2] = coverage;
            bgra[i + 3] = coverage;
        }
    }

    /// <summary>
    /// 模糊后强制为预乘白色，去掉被黑底污染的灰边。
    /// </summary>
    private static void ForcePremultipliedWhiteGlow(byte[] bgra)
    {
        for (var i = 0; i < bgra.Length; i += 4)
        {
            var alpha = bgra[i + 3];
            bgra[i + 0] = alpha;
            bgra[i + 1] = alpha;
            bgra[i + 2] = alpha;
        }
    }

    private void ClearGlowBitmapCache()
    {
        foreach (var entry in _glowBitmapCache.Values)
        {
            entry.Bitmap.Dispose();
        }

        _glowBitmapCache.Clear();
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
        // 测量阶段使用这里的最坏情况预留顶部空间，避免上浮、放大时被裁剪
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
            // 辉光柔边改为行内裁剪，不再额外抬高行高，避免多行歌词被撑开或溢出
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
            // 上浮是一次性的状态推进：到达顶部后保持 1，不再回落
            return 1;
        }

        var progress = GetProgress(position, start, end);
        return 1 - Math.Pow(1 - progress, 3);
    }

    private static bool ShouldUseWholeWordLift(IReadOnlyList<CharacterTiming> timings)
    {
        // 只要一个字符的有效时间不足阈值，就统一退化为整词上浮，避免同一单词内混用两种节奏
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

        // 同时计算字符缩放产生的外扩和强调左右位移产生的外扩，取整行最大值
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

        // 辉光柔边在行内裁剪，宽度也不再为模糊额外预留
        return maximumHorizontalSpace;
    }

    internal static bool IsWordEmphasisEligible(LyricsWord word)
    {
        // 单词时间恰好 1 秒不触发
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

    /// <summary>
    /// AMLL 的 blur 曲线：以 3 秒为分界，短词三次方压低，长词平方根平缓增长；
    /// 再乘基础系数与末词增益，上限 0.8。
    /// </summary>
    internal static double GetWordEmphasisBlur(
        TimeSpan start,
        TimeSpan end,
        bool isLastWord)
    {
        var durationMilliseconds = Math.Max(
            WordEmphasisMinimumDurationMilliseconds,
            (end - start).TotalMilliseconds);
        var blur = durationMilliseconds / WordEmphasisBlurDurationDivisor;
        blur = blur > 1 ? Math.Sqrt(blur) : Math.Pow(blur, 3);
        blur *= WordEmphasisBlurFactor;
        if (isLastWord)
        {
            blur *= WordEmphasisLastWordBlurFactor;
        }

        return Math.Min(WordEmphasisMaximumBlur, blur);
    }

    /// <summary>
    /// AMLL text-shadow 不透明度：empEasing(x) * blur。
    /// </summary>
    internal static double GetEmphasisGlowLevel(double blur, double response)
    {
        if (blur <= 0 || response <= 0)
        {
            return 0;
        }

        return Math.Clamp(blur * response, 0, 1);
    }

    /// <summary>
    /// AMLL text-shadow 模糊半径：min(0.3, blur * 0.3) em。
    /// </summary>
    internal static double GetEmphasisGlowRadius(double fontSize, double blur)
    {
        if (fontSize <= 0 || blur <= 0)
        {
            return 0;
        }

        var em = Math.Min(
            WordEmphasisGlowRadiusEmCap,
            Math.Clamp(blur, 0, WordEmphasisMaximumBlur) * WordEmphasisGlowRadiusBlurFactor);
        return fontSize * em;
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

        // 第一项跟随缩放曲线，第二项参考 AMLL 的正弦漂浮；两者共用同一时间窗口，
        // 确保缩放结束时额外位移也回到 0，只保留此前已经完成的持续上浮位置
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

        // AMLL 使用两段不同的贝塞尔曲线：前半段快速升至峰值，后半段更从容地回到原始大小
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
        // 贝塞尔控制点给出的是参数 t 对应的横纵坐标。调用方传入的是时间进度，
        // 因此先用二分法反求参数 t，再取同一 t 上的纵坐标，行为才与常见贝塞尔缓动一致
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
        // 使宽字符获得更长的高亮/上浮时间，窄字符不会拖慢整词推进
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

        // 只有歌词对象、文本或字体变化时才重建排版缓存；播放进度的逐帧变化只触发重绘
        ClearGlowBitmapCache();
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
        // 第一轮确定每个单词在整行中的位置，第二轮再拆分字素簇并生成两套字符时间轴
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
            // 字符段决定裁剪区域；字符时间按宽度分配；强调时间只按字符索引错相
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
        // 这里按 Unicode 字素簇拆分，而不是按 UTF-16 码元拆分，避免拆开表情符号、组合音标等
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
                // 空白不单独做动画：词尾空白并入前一字符，词首空白并入后一字符，保证整体宽度连续
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
                    leadingWhitespaceWidth + width,
                    textElement));
                leadingWhitespaceWidth = 0;
            }

            cursor += width;
        }

        if (segments.Count == 0)
        {
            return [new CharacterSegment(0, targetWidth, text)];
        }

        if (cursor <= 0)
        {
            var uniformWidth = targetWidth / segments.Count;
            return segments
                .Select((segment, index) => new CharacterSegment(
                    index * uniformWidth,
                    uniformWidth,
                    segment.Text))
                .ToArray();
        }

        // 单字符测量之和可能因字距与整词测量不同，统一缩放回单词的真实目标宽度
        var scale = targetWidth / cursor;
        return segments
            .Select(segment => new CharacterSegment(segment.Start * scale, segment.Width * scale, segment.Text))
            .ToArray();
    }

    internal static string[] SplitTextElements(string text)
    {
        // 解析组合字符，得到每个 Unicode 字素簇在字符串中的起始位置
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

    private static IBrush CreateMutedBrush(IBrush foreground) =>
        CreateOpacityBrush(foreground, 0.38);

    private static IBrush CreateOpacityBrush(IBrush foreground, double opacity)
    {
        if (foreground is ISolidColorBrush solid)
        {
            return new SolidColorBrush(solid.Color, opacity);
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

    private readonly record struct GlowBitmapCacheEntry(Bitmap Bitmap, double Pad);

    // 单个字素簇在所属单词内的水平起点、宽度与文本，用于裁剪与单字辉光
    private readonly record struct CharacterSegment(double Start, double Width, string Text);

    // 单个动画窗口的绝对播放时间；高亮/上浮与强调各自维护一套窗口
    private readonly record struct CharacterTiming(TimeSpan Start, TimeSpan End);

}
