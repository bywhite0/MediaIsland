using System.Collections.Immutable;
using Microsoft.Extensions.Logging;

namespace MediaIsland.Services.Audio;

/// <summary>
/// 把一个 <see cref="IAudioFrameSource"/> 的帧分发给若干 <see cref="IAudioFrameSink"/>，
/// 并按外部给定的需求启停采集。
///
/// 采集启停由需求状态驱动，不由 sink 数量驱动。这两者容易混淆但语义不同：
/// 采集需求来自协议层（谁订阅了 audio、谁发过 play_start），而 sink 是本地消费者——
/// 第 3 期的可视化 sink 会常驻，它不该让麦克风/扬声器端点永远被占着。
///
/// <see cref="SetCaptureDemandAsync"/> 接受一个布尔状态而非 start/stop 指令，
/// 故调用方可以无脑重算并调用，重复传相同值是 no-op。这与
/// <c>MediaLinkSessionHub.HasDownstreamAudioDemand</c> 的重算取向一致：
/// 状态函数没有需要精确配对的「路径」。
/// </summary>
public sealed class AudioFrameHub : IDisposable
{
    private readonly IAudioFrameSource _source;
    private readonly ILogger? _logger;
    private readonly object _sinkGate = new();
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private ImmutableArray<IAudioFrameSink> _sinks = [];
    private bool _capturing;
    private bool _disposed;

    public AudioFrameHub(IAudioFrameSource source, ILogger? logger = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _logger = logger;
        _source.FrameAvailable += OnFrameAvailable;
    }

    public int SinkCount => _sinks.Length;

    public bool IsCapturing => Volatile.Read(ref _capturing);

    public bool IsSourceAvailable => _source.IsAvailable;

    public string? SourceFailureReason => _source.FailureReason;

    /// <summary>
    /// 注册一个帧汇，释放返回值即摘除。用 <see cref="IDisposable"/> 而非 Remove 方法，
    /// 是为了让注册方无法忘记摘除——`using` 或字段持有都能表达生命周期。
    /// </summary>
    public IDisposable AddSink(IAudioFrameSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);

        lock (_sinkGate)
        {
            _sinks = _sinks.Add(sink);
        }

        return new Subscription(this, sink);
    }

    /// <summary>
    /// 设置采集需求。幂等：重复传相同值不触碰源。
    ///
    /// 启动失败不向外抛：音频是可选能力，采集起不来时其余频道必须继续服务。
    /// 失败后 <see cref="IsCapturing"/> 停在假，故下一次重算仍会尝试启动，具备自愈能力。
    /// </summary>
    public async Task SetCaptureDemandAsync(bool demanded, CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }

        await _captureGate.WaitAsync(cancellationToken);
        try
        {
            if (demanded && !_capturing)
            {
                if (!_source.IsAvailable)
                {
                    _logger?.LogDebug("[音频] 采集源不可用，跳过启动：{Reason}", _source.FailureReason);
                    return;
                }

                try
                {
                    await _source.StartAsync(cancellationToken);
                    Volatile.Write(ref _capturing, true);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "[音频] 采集启动失败");
                }
            }
            else if (!demanded && _capturing)
            {
                Volatile.Write(ref _capturing, false);
                try
                {
                    await _source.StopAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "[音频] 采集停止时出错");
                }
            }
        }
        finally
        {
            _captureGate.Release();
        }
    }

    /// <summary>
    /// 分发。在采集线程上同步执行，故不得阻塞——单个 sink 抛异常只记日志，
    /// 绝不打断其余 sink：它们之间没有任何依赖，一个崩掉不该让全部停摆。
    ///
    /// 异常的 sink 不自动摘除：瞬时异常不等价于注销，自动摘除会把一次偶发失败
    /// 变成永久静默，且 sink 无从得知自己已被摘除。
    /// </summary>
    private void OnFrameAvailable(AudioFrame frame)
    {
        // 停止后仍可能收到在途帧（采集线程尚未完全退出），此时不再投递。
        if (!Volatile.Read(ref _capturing))
        {
            return;
        }

        // 快照读，分发路径无锁：50 帧/秒的热路径不该与注册/摘除抢锁。
        var sinks = _sinks;
        foreach (var sink in sinks)
        {
            try
            {
                var pending = sink.OnFrameAsync(frame, CancellationToken.None);
                if (!pending.IsCompletedSuccessfully)
                {
                    // 同步完成是常态（入队而非发送）。异步路径不等待，否则会阻塞采集线程。
                    _ = pending.AsTask().ContinueWith(
                        t => _logger?.LogDebug(t.Exception, "[音频] sink 异步处理失败"),
                        TaskContinuationOptions.OnlyOnFaulted);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "[音频] sink 处理帧失败");
            }
        }
    }

    private void RemoveSink(IAudioFrameSink sink)    {
        lock (_sinkGate)
        {
            _sinks = _sinks.Remove(sink);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _source.FrameAvailable -= OnFrameAvailable;

        // 停服路径必须无条件停采集，否则插件禁用后音频端点仍被占着。
        if (Volatile.Read(ref _capturing))
        {
            Volatile.Write(ref _capturing, false);
            try
            {
                _source.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "[音频] 释放时停止采集出错");
            }
        }

        lock (_sinkGate)
        {
            _sinks = [];
        }

        _captureGate.Dispose();
    }

    private sealed class Subscription(AudioFrameHub hub, IAudioFrameSink sink) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                hub.RemoveSink(sink);
            }
        }
    }
}
