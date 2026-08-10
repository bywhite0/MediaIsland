using Microsoft.Extensions.Logging;

namespace MediaIsland.Services.MediaLink;

/// <summary>
/// 上游音频帧的本机消费与向下游的转发。
///
/// 转发是同一个 byte[] 的引用透传，不解码、不重组、不改写帧头。上游的三个时间戳
/// 与 seq 原样保留——改写它们会抹掉上游那一跳的时间信息，下游算出的传输延迟
/// 就只覆盖最后一跳。帧长度自描述，包大小不均匀无害。
///
/// 失配帧不转发：一帧连本机都判它属于过期曲目，转出去只会让下游做同样的判定。
///
/// 独立成类而非并进宿主服务，是因为端到端驱动这三条分支需要构造宿主服务，
/// 而它需要媒体源仲裁器，后者又需要媒体服务与歌词服务。为验证十行分支
/// 拖进整个媒体与歌词子系统，测的东西会远多于要测的东西。
/// </summary>
public sealed class MediaLinkAudioRelay(
    MediaLinkAudioReceiver receiver,
    Func<byte[], CancellationToken, Task>? forwarder,
    ILogger? logger = null)
{
    private readonly MediaLinkAudioReceiver _receiver =
        receiver ?? throw new ArgumentNullException(nameof(receiver));

    private long _forwardedFrames;

    /// <summary>已成功转发的帧数。用于诊断链路通没通，与接收计数分开看。</summary>
    public long ForwardedFrames => Interlocked.Read(ref _forwardedFrames);

    public async Task HandleAsync(byte[] frame, CancellationToken cancellationToken = default)
    {
        MediaLinkAudioRejectReason reason;
        try
        {
            reason = _receiver.Handle(frame);
        }
        catch (Exception ex)
        {
            // 单帧处理失败不该拖垮收循环——那会把一次数据问题升级成一次断连。
            logger?.LogDebug(ex, "[音频] 处理上游音频帧失败");
            return;
        }

        if (reason != MediaLinkAudioRejectReason.None || forwarder is null)
        {
            return;
        }

        try
        {
            await forwarder(frame, cancellationToken);
            Interlocked.Increment(ref _forwardedFrames);
        }
        catch (Exception ex)
        {
            // 转发失败是下游连接的问题。让它冒泡会把一次下游故障升级成本机音频全失效。
            logger?.LogDebug(ex, "[音频] 转发上游音频帧失败");
        }
    }
}
