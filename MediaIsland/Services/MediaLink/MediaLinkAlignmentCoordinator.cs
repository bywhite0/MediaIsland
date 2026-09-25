using MediaIsland.Services.Audio;
using MediaIsland.Services.Audio.Playback;
using Microsoft.Extensions.Logging;

namespace MediaIsland.Services.MediaLink;

/// <summary>
/// 最近一次对齐判定的快照，诊断面的单一真相源：设置页状态行只显示它，不自己重算
/// 归因——归因逻辑长两份，改一处忘另一处时页面与日志就各说各话。值语义整体复制，
/// 读方拿到的是锁内那一刻的成组结论，不会读到半新半旧。
/// </summary>
/// <param name="Enabled">判定那一刻的用户开关。关着时四态结论只是推演，显示方据此改口。</param>
/// <param name="HasStarted">
/// 判定那一刻已出过声。出声前 Aligned 是未经设备事实核验的乐观结论，
/// 显示方与日志同一纪律：不宣布。
/// </param>
/// <param name="State">四态归因。</param>
/// <param name="Reason">四态的用户可读文案，与日志落的是同一份。</param>
/// <param name="PlayTimeErrorUs">外环误差，微秒，有符号。未起播或未对齐时无意义。</param>
/// <param name="TargetMsCurrent">抖动缓冲目标深度当前值，毫秒。未起播时为零。</param>
/// <param name="MinTargetMs">目标深度区间下端，渲染实现的常量。</param>
/// <param name="MaxTargetMs">目标深度区间上端，渲染实现的常量。</param>
internal readonly record struct MediaLinkAlignmentSnapshot(
    bool Enabled,
    bool HasStarted,
    MediaLinkAlignmentState State,
    string Reason,
    long PlayTimeErrorUs,
    int TargetMsCurrent,
    int MinTargetMs,
    int MaxTargetMs);

/// <summary>
/// 有状态的对齐协调方：把「服务端声明的预算」「本机设备事实」「对时结果」「用户开关
/// 与当前设备的手动偏移」几路输入捏成一次判定，并把结论下发给播放侧。它是
/// <see cref="MediaLinkAlignmentPolicy"/>
/// 唯一的生产调用点——判定是纯函数，谁在什么时候拿什么喂它、结论去哪，都在这里。
///
/// 触发全靠外部：hello 到达或重发、每轮对时探测、音源仲裁变化，都各自调一次
/// <see cref="Recompute"/>。重算读的全是当前状态而非增量，重复触发只是多算一次，
/// 不会累积出错误状态——这与两个宿主服务既有的重算约定同构。
///
/// 输入以委托注入而不是持有客户端实例：客户端随设置热更新反复重建，协调方跟着它建
/// 就要把订阅、退订、置换的时序全背一遍；委托每次读「当下那个」，重建对它不存在。
/// </summary>
internal sealed class MediaLinkAlignmentCoordinator
{
    private readonly AudioPlaybackService _playback;
    private readonly Func<MediaLinkServerDeclaration> _declaration;
    private readonly Func<(bool Available, long OffsetTicks)> _wireOffset;
    private readonly Func<bool> _alignmentEnabled;
    private readonly Func<string?, int> _manualOffsetMs;
    private readonly ILogger? _logger;
    private readonly object _gate = new();

    /// <summary>
    /// 上次落进日志的状态与方向。归因出口只记转移，不逐秒重复同一句话；
    /// 方向位参与去重是因为 BudgetTooSmall 的两个方向共用一个状态——
    /// 只按状态去重时方向互换不落新日志，旧警告会把「该往哪边改」指反。
    /// </summary>
    private (MediaLinkAlignmentState State, bool BudgetExceedsCap)? _lastLogged;

    /// <summary>
    /// 上次是否已宣布饱和。饱和出口与四态归因同形，只记转移：进入一条、恢复一条、
    /// 中间静默。开关关着时清掉，关了再开要重新宣布当时的状态。
    /// </summary>
    private bool _lastSaturated;

    private MediaLinkAlignmentSnapshot? _lastDecision;

    /// <summary>
    /// 饱和的单端误差预算，微秒。目标深度贴住区间端点说明外环行程已用尽，此时误差
    /// 仍超这个数才算饱和——贴边但误差已收进预算只是恰好收敛到边界，不是故障。
    /// 5ms 即单端稳态误差预算：两端差 10ms 目标的一半，真机门控与 native 外环
    /// 注释用的同一个分配额。
    /// </summary>
    private const long SaturationErrorBudgetUs = 5_000;

    internal MediaLinkAlignmentCoordinator(
        AudioPlaybackService playback,
        Func<MediaLinkServerDeclaration> declaration,
        Func<(bool Available, long OffsetTicks)> wireOffset,
        Func<bool> alignmentEnabled,
        Func<string?, int> manualOffsetMs,
        ILogger? logger = null)
    {
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));
        _declaration = declaration ?? throw new ArgumentNullException(nameof(declaration));
        _wireOffset = wireOffset ?? throw new ArgumentNullException(nameof(wireOffset));
        _alignmentEnabled = alignmentEnabled ?? throw new ArgumentNullException(nameof(alignmentEnabled));
        _manualOffsetMs = manualOffsetMs ?? throw new ArgumentNullException(nameof(manualOffsetMs));
        _logger = logger;
    }

    /// <summary>
    /// 最近一次判定的快照。锁内整体复制出去，读方与 Recompute 抢的是同一把
    /// <see cref="_gate"/>，拿到的必是某一次重算的成组结论。还没重算过时为 null，
    /// 显示方以「暂无」呈现。
    /// </summary>
    internal MediaLinkAlignmentSnapshot? LastDecision
    {
        get
        {
            lock (_gate)
            {
                return _lastDecision;
            }
        }
    }

    /// <summary>
    /// 重算一次并下发。开关与可行性分属两个时点：开关（enabled）无条件下发——它决定
    /// 起播那一刻建不建内环的执行器，必须在起播前就位；可行性判不过时把 offset 与 D
    /// 置零让外环不动，声音照出、只是不对齐，原因走日志。
    /// </summary>
    internal void Recompute()
    {
        lock (_gate)
        {
            try
            {
                RecomputeUnlocked();
            }
            catch (Exception ex)
            {
                // 对齐是增强项。重算的意外（读设备事实撞上任何异常）不该沿着触发它的
                // 事件链上溯——那条链的另一端是音源仲裁与连接生命周期，炸在那里
                // 的症状与对齐毫无关联。
                _logger?.LogDebug(ex, "[音频:对齐] 重算失败，保持上一次下发的参数");
            }
        }
    }

    private void RecomputeUnlocked()
    {
        var declaration = _declaration();
        var enabled = _alignmentEnabled();
        var facts = _playback.ReadAlignmentFacts();
        var (offsetAvailable, offsetTicks) = _wireOffset();

        var decision = MediaLinkAlignmentPolicy.Decide(
            declaration.SupportsAudioClock,
            declaration.AudioClockBudgetMs,
            facts.DeviceLatencyMs,
            facts.DeviceBufferMs,
            facts.MinTargetMs,
            offsetAvailable);

        // 手动偏移与 enabled 一样无条件下发，不夹在 aligned 后面：它是当前设备的
        // 硬件尾段这个事实，不是可行性结论，预算装不装得下都不改变它。native 只在
        // 误差计算里用它，而那段被 offset 可用性闸住——判不过时它无处施力，
        // 归零它只是让恢复对齐的那一刻多一次参数摆动。
        // 毫秒换 100ns 与 D 同式；±500ms 的区间在设置层已夹紧，这里不会溢出。
        var manualOffsetTicks = _manualOffsetMs(_playback.CurrentPlaybackDeviceId)
            * MonotonicClock.TicksPerMs100Ns;

        // D 与 offset 只在判定通过时下发。判不过还带着值，外环就会拿着一个已被
        // 判死的目标继续走步——那正是「假装对齐」的形态。上界已在判定里挡过，
        // 这里换算 100ns 不会溢出。
        var aligned = decision.IsAligned;
        _playback.ConfigureAlignment(
            enabled,
            aligned && declaration.AudioClockBudgetMs is { } budgetMs
                ? budgetMs * MonotonicClock.TicksPerMs100Ns
                : 0,
            aligned ? offsetTicks : 0,
            manualOffsetTicks);

        var bounds = _playback.TargetDepthBounds();
        _lastDecision = new MediaLinkAlignmentSnapshot(
            enabled,
            facts.HasStarted,
            decision.State,
            decision.Reason,
            facts.PlayTimeErrorUs,
            facts.TargetMsCurrent,
            bounds.MinTargetMs,
            bounds.MaxTargetMs);

        LogTransition(enabled, facts.HasStarted, decision);
        LogSaturation(enabled, decision, facts, bounds);
    }

    /// <summary>
    /// 归因出口。只在开关开着时说话（关着时四态归因没有听众，反而刷日志）；
    /// 只记状态转移；且不在出声前宣布「正在对齐」——那时设备事实还是零，
    /// 结论未经可行性检验，起播后的第一次重算才作数。
    /// </summary>
    private void LogTransition(bool enabled, bool hasStarted, MediaLinkAlignmentDecision decision)
    {
        if (!enabled)
        {
            // 开关关了再开要重新说一遍当时的状态，故清掉去重水位。
            _lastLogged = null;
            return;
        }

        if ((decision.State, decision.BudgetExceedsCap) == _lastLogged
            || (decision.IsAligned && !hasStarted))
        {
            return;
        }

        _lastLogged = (decision.State, decision.BudgetExceedsCap);
        if (decision.IsAligned)
        {
            _logger?.LogInformation("[音频:对齐] {Reason}", decision.Reason);
        }
        else
        {
            // 退回记警告，与「退回非对齐模式并记警告」的协议行为约定一致。
            _logger?.LogWarning("[音频:对齐] {Reason}", decision.Reason);
        }
    }

    /// <summary>
    /// 饱和出口。外环把目标深度拧到区间端点、误差却仍超单端预算，说明偏差超出它的
    /// 行程——那是硬重置的前兆。规则原文挂在 native 侧 is_saturated 的文档上
    /// （native 无日志设施），这里是它唯一的生产落点。三个条件缺一不可：未对齐时
    /// stats 是残值；贴边而误差在预算内是恰好收敛到边界；不贴边说明外环还有行程。
    /// 与四态归因同形，只记转移；误差在预算线附近往返时不设迟滞，抖动最多让
    /// 进入与恢复成对出现，可接受。
    /// </summary>
    private void LogSaturation(
        bool enabled,
        MediaLinkAlignmentDecision decision,
        AudioAlignmentFacts facts,
        (int MinTargetMs, int MaxTargetMs) bounds)
    {
        if (!enabled)
        {
            // 开关关着时不判也不说话，与四态归因同一条纪律；水位一并清掉，
            // 关了再开要重新宣布当时的状态。
            _lastSaturated = false;
            return;
        }

        var atBound = facts.TargetMsCurrent <= bounds.MinTargetMs
            || facts.TargetMsCurrent >= bounds.MaxTargetMs;
        var saturated = decision.IsAligned
            && atBound
            && Math.Abs(facts.PlayTimeErrorUs) > SaturationErrorBudgetUs;
        if (saturated == _lastSaturated)
        {
            return;
        }

        _lastSaturated = saturated;
        if (saturated)
        {
            _logger?.LogWarning(
                "[音频:对齐] 外环饱和：目标深度 {TargetMs}ms 已贴住区间 {MinMs}-{MaxMs}ms 端点，"
                + "误差仍有 {ErrorUs}us，偏差超出外环能力，可能预示硬重置",
                facts.TargetMsCurrent,
                bounds.MinTargetMs,
                bounds.MaxTargetMs,
                facts.PlayTimeErrorUs);
        }
        else
        {
            _logger?.LogInformation("[音频:对齐] 外环饱和解除");
        }
    }
}
