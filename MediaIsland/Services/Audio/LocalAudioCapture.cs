using Microsoft.Extensions.Logging;

namespace MediaIsland.Services.Audio;

/// <summary>
/// 本机采集的唯一归属，随插件常驻。
///
/// 此前采集源与 <see cref="AudioFrameHub"/> 跟着 MediaLink 服务端一起建、一起毁，
/// 于是关掉「Link to the MEDIA」共享后，岛上的音频组件永远拿不到本机声音——
/// 在本机画频谱从原理上并不需要网络服务，那条依赖只是实现上的巧合。
///
/// 可视化 sink 在构造时挂上且常驻；广播器由服务端在运行期间挂上、停服时摘下。
/// 常驻 sink 不会让设备一直被占：启停只由 <see cref="SetDemandAsync"/> 的需求决定，
/// 与 sink 数量无关（<see cref="AudioFrameHub"/> 的既有语义）。
/// </summary>
public sealed class LocalAudioCapture : IDisposable
{
    private readonly IAudioFrameSource _source;
    private readonly AudioFrameHub _hub;
    private readonly IDisposable? _visualizationSubscription;

    public LocalAudioCapture(
        IAudioFrameSource source,
        IAudioFrameSink? visualization = null,
        ILoggerFactory? loggerFactory = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _hub = new AudioFrameHub(source, loggerFactory?.CreateLogger<AudioFrameHub>());
        if (visualization is not null)
        {
            _visualizationSubscription = _hub.AddSink(visualization);
        }

        if (!source.IsAvailable)
        {
            loggerFactory?.CreateLogger<LocalAudioCapture>().LogInformation(
                "[音频] 本机采集不可用（{Reason}），上游音频与其余功能不受影响", source.FailureReason);
        }
    }

    public bool IsCapturing => _hub.IsCapturing;

    public bool IsSourceAvailable => _hub.IsSourceAvailable;

    public string? SourceFailureReason => _hub.SourceFailureReason;

    /// <summary>挂一个额外的帧汇（MediaLink 广播器）。释放返回值即摘除。</summary>
    public IDisposable AttachSink(IAudioFrameSink sink) => _hub.AddSink(sink);

    /// <summary>当前挂着的帧汇数。只供测试观察广播器是否随共享开关挂上/摘下。</summary>
    internal int SinkCount => _hub.SinkCount;

    /// <summary>重算并应用采集需求。幂等：重复传相同结果不触碰源。</summary>
    public Task SetDemandAsync(bool protocolDemand, bool visualizationDemand, bool externalMediaEffective) =>
        _hub.SetCaptureDemandAsync(
            ShouldCaptureLocally(protocolDemand, visualizationDemand, externalMediaEffective),
            CancellationToken.None);

    /// <summary>默认渲染端点变化后的跟随入口。未在采集时是空操作。</summary>
    public Task RestartAsync() => _hub.RestartCaptureAsync(CancellationToken.None);

    /// <summary>
    /// 采集需求的全部逻辑：两个需求来源取并集，再整体减去「当前生效的媒体来自上游」。
    /// 那首歌来自上游时本机该转发而不是采集自己的输出，否则下游收到的是本机 PCM
    /// 配上游曲目标识，校验能过但内容错配。第三个入参是仲裁结果而非连接态。
    /// </summary>
    internal static bool ShouldCaptureLocally(
        bool protocolDemand, bool visualizationDemand, bool externalMediaEffective) =>
        (protocolDemand || visualizationDemand) && !externalMediaEffective;

    public void Dispose()
    {
        _visualizationSubscription?.Dispose();
        // hub 的 Dispose 同步停采集；源随后释放 native 句柄。顺序不能反。
        _hub.Dispose();
        (_source as IDisposable)?.Dispose();
    }
}
