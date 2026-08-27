using MediaIsland.Services.Audio;
using MediaIsland.Services.Audio.Playback;
using Microsoft.Extensions.Logging;

namespace MediaIsland.Services.MediaLink;

/// <summary>
/// 有状态的对齐协调方：把「服务端声明的预算」「本机设备事实」「对时结果」「用户开关」
/// 四路输入捏成一次判定，并把结论下发给播放侧。它是 <see cref="MediaLinkAlignmentPolicy"/>
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
    private readonly ILogger? _logger;
    private readonly object _gate = new();

    /// <summary>
    /// 上次落进日志的状态与方向。归因出口只记转移，不逐秒重复同一句话；
    /// 方向位参与去重是因为 BudgetTooSmall 的两个方向共用一个状态——
    /// 只按状态去重时方向互换不落新日志，旧警告会把「该往哪边改」指反。
    /// </summary>
    private (MediaLinkAlignmentState State, bool BudgetExceedsCap)? _lastLogged;

    internal MediaLinkAlignmentCoordinator(
        AudioPlaybackService playback,
        Func<MediaLinkServerDeclaration> declaration,
        Func<(bool Available, long OffsetTicks)> wireOffset,
        Func<bool> alignmentEnabled,
        ILogger? logger = null)
    {
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));
        _declaration = declaration ?? throw new ArgumentNullException(nameof(declaration));
        _wireOffset = wireOffset ?? throw new ArgumentNullException(nameof(wireOffset));
        _alignmentEnabled = alignmentEnabled ?? throw new ArgumentNullException(nameof(alignmentEnabled));
        _logger = logger;
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
                _logger?.LogDebug(ex, "[音频:对齐] 重算失败，保持上一次下发的参数。");
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
            manualOffsetTicks: 0);

        LogTransition(enabled, facts.HasStarted, decision);
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
}
