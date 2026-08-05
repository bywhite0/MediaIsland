using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace MediaIsland.Controls
{
    /// <summary>
    /// 单行内容的溢出裁剪 + 滚动宿主。
    /// <para>
    /// 组件为支持负边距把 ClipToBounds 关成 False，宽度上界只能加在内容层，所以这里自行裁剪。
    /// 每个实例只负责自己包裹的那一行：标题过长时只有标题滚动，艺术家不受牵连。
    /// </para>
    /// <para>
    /// 常规面板会把 MaxWidth 约束下传，子元素宽度随之被 clamp，于是既拿不到内容自然宽度也无法
    /// 平移。<see cref="IsScrollEnabled"/> 为 true 时改用无限宽度测量子元素，让它按自然宽度
    /// 布局并溢出本控件，再靠 Clip 裁掉多余部分。
    /// </para>
    /// <para>
    /// 裁剪与平移必须分处两个元素：Avalonia 的 ClipToBounds 在元素自身坐标系内生效，若把
    /// RenderTransform 放在同一元素上，裁剪窗口会随内容一起平移，于是只看到整行滑动、
    /// 被裁掉的部分永远露不出来。因此本控件负责裁剪，平移施加在 Child 上。
    /// </para>
    /// </summary>
    public class OverflowScrollHost : Decorator
    {
        /// <summary>每秒平移像素数。取值偏慢，避免在状态栏里造成视觉噪音。</summary>
        private const double SpeedPixelsPerSecond = 24;

        /// <summary>到达一端后的停留时间，让用户有时间读完首尾。</summary>
        private static readonly TimeSpan DwellDuration = TimeSpan.FromSeconds(1.2);

        /// <summary>低于该值的溢出不值得滚动，避免亚像素抖动。</summary>
        private const double MinOverflow = 1.0;

        public static readonly StyledProperty<bool> IsScrollEnabledProperty =
            AvaloniaProperty.Register<OverflowScrollHost, bool>(nameof(IsScrollEnabled));

        private readonly TranslateTransform _transform = new();
        private DispatcherTimer? _timer;

        private double _overflow;
        private double _offset;
        private bool _movingForward = true;
        private TimeSpan _dwellRemaining = DwellDuration;

        static OverflowScrollHost()
        {
            AffectsMeasure<OverflowScrollHost>(IsScrollEnabledProperty);
            ClipToBoundsProperty.OverrideDefaultValue<OverflowScrollHost>(true);
        }

        public OverflowScrollHost()
        {
        }

        /// <summary>为 true 时内容按自然宽度布局并在溢出时来回滚动，否则交给 TextTrimming 截断。</summary>
        public bool IsScrollEnabled
        {
            get => GetValue(IsScrollEnabledProperty);
            set => SetValue(IsScrollEnabledProperty, value);
        }

        /// <summary>最近一次测量得到的内容自然宽度。</summary>
        public double NaturalWidth { get; private set; }

        /// <summary>当前是否正在滚动，供测试与诊断使用。</summary>
        public bool IsScrolling => _timer?.IsEnabled == true;

        protected override Size MeasureOverride(Size availableSize)
        {
            var child = Child;
            if (child is null)
            {
                NaturalWidth = 0;
                return default;
            }

            var childConstraint = IsScrollEnabled
                ? new Size(double.PositiveInfinity, availableSize.Height)
                : availableSize;

            child.Measure(childConstraint);
            NaturalWidth = child.DesiredSize.Width;

            // 对外只申报可用宽度以内的部分，超出的由 Clip 裁掉。
            var width = double.IsFinite(availableSize.Width)
                ? Math.Min(NaturalWidth, availableSize.Width)
                : NaturalWidth;

            return new Size(width, child.DesiredSize.Height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var child = Child;
            if (child is null)
            {
                UpdateScrollState(0);
                return finalSize;
            }

            var width = IsScrollEnabled ? Math.Max(NaturalWidth, finalSize.Width) : finalSize.Width;
            child.Arrange(new Rect(0, 0, width, finalSize.Height));

            // 平移施加在 Child 上，裁剪留在本控件，两者分处不同元素才能真正露出溢出部分。
            if (!ReferenceEquals(child.RenderTransform, _transform))
            {
                child.RenderTransform = _transform;
            }

            UpdateScrollState(ResolveOverflow(IsScrollEnabled, finalSize.Width, NaturalWidth));
            return finalSize;
        }

        /// <summary>
        /// 纯计算：需要滚动时返回溢出像素数，否则返回 0。与 UI 无关，便于单测。
        /// </summary>
        public static double ResolveOverflow(bool enabled, double viewportWidth, double contentWidth)
        {
            if (!enabled) return 0;
            if (!double.IsFinite(viewportWidth) || !double.IsFinite(contentWidth)) return 0;
            if (viewportWidth <= 0 || contentWidth <= 0) return 0;

            var overflow = contentWidth - viewportWidth;
            return overflow >= MinOverflow ? overflow : 0;
        }

        /// <summary>
        /// 纯计算：推进一帧后的位移与方向。停留期间位移不变，只递减停留计时。
        /// </summary>
        public static (double Offset, bool MovingForward, TimeSpan DwellRemaining) Advance(
            double offset,
            bool movingForward,
            TimeSpan dwellRemaining,
            double overflow,
            TimeSpan delta)
        {
            if (overflow <= 0)
            {
                return (0, true, TimeSpan.Zero);
            }

            if (dwellRemaining > TimeSpan.Zero)
            {
                var left = dwellRemaining - delta;
                return (offset, movingForward, left > TimeSpan.Zero ? left : TimeSpan.Zero);
            }

            var step = SpeedPixelsPerSecond * delta.TotalSeconds;
            var next = movingForward ? offset + step : offset - step;

            if (next >= overflow)
            {
                return (overflow, false, DwellDuration);
            }

            if (next <= 0)
            {
                return (0, true, DwellDuration);
            }

            return (next, movingForward, TimeSpan.Zero);
        }

        /// <summary>
        /// 每次 arrange 后同步滚动状态。arrange 由布局系统驱动，因此设置变更、换歌、
        /// 字号调整都会自动走到这里，无需组件手工接线。
        /// </summary>
        private void UpdateScrollState(double overflow)
        {
            if (overflow <= 0)
            {
                StopScrolling();
                return;
            }

            // 溢出量变了（换歌、改字号）就从头滚，否则保持当前进度避免视觉跳变。
            if (Math.Abs(overflow - _overflow) > 0.5)
            {
                _overflow = overflow;
                ResetPosition();
            }

            _timer ??= new DispatcherTimer(
                TimeSpan.FromSeconds(1.0 / 30),
                DispatcherPriority.Render,
                OnTick);

            if (!_timer.IsEnabled)
            {
                _timer.Start();
            }
        }

        private void StopScrolling()
        {
            // arrange 每帧都可能调到这里，未在滚动时直接返回，避免无谓的属性写入。
            if (_timer?.IsEnabled != true && _overflow == 0 && _offset == 0)
            {
                return;
            }

            _timer?.Stop();
            _overflow = 0;
            ResetPosition();
        }

        private void ResetPosition()
        {
            _offset = 0;
            _movingForward = true;
            _dwellRemaining = DwellDuration;
            _transform.X = 0;
        }

        private void OnTick(object? sender, EventArgs e)
        {
            var (offset, movingForward, dwell) = Advance(
                _offset, _movingForward, _dwellRemaining, _overflow, TimeSpan.FromSeconds(1.0 / 30));

            _offset = offset;
            _movingForward = movingForward;
            _dwellRemaining = dwell;
            _transform.X = -offset;
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);

            // 组件被移出可视树后必须停表，否则计时器会让实例一直存活。
            if (_timer is not null)
            {
                _timer.Stop();
                _timer.Tick -= OnTick;
                _timer = null;
            }

            _overflow = 0;
            ResetPosition();
        }
    }
}
