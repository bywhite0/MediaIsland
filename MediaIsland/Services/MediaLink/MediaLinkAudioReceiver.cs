using System.Diagnostics;
using MediaIsland.Services.Audio;
using MediaIsland.Services.MediaLink.Protocol;
using Microsoft.Extensions.Logging;

namespace MediaIsland.Services.MediaLink;

/// <summary>
/// 入站音频帧被拒的原因。三者语义不同，都不该断连：
/// <see cref="Malformed"/> 是帧本身坏了，<see cref="OddPcmLength"/> 是结构对但载荷不可解释，
/// <see cref="TrackMismatch"/> 是帧完全正确但已经过期。全是数据问题而非连接问题。
/// </summary>
public enum MediaLinkAudioRejectReason
{
    None,
    Malformed,
    OddPcmLength,
    TrackMismatch
}

/// <summary>
/// 上游音频帧 → 托管 <see cref="AudioFrame"/>。这是接收侧的唯一接缝，
/// 与发送侧的 <see cref="MediaLinkAudioBroadcaster"/> 结构对称：
/// 一个盖 trackToken 与三时间戳后编码，一个解码校验后剥回。
/// 两侧之外，<c>Services/Audio</c> 与 <c>Protocol</c> 都不知道对方存在。
///
/// <see cref="Handle"/> 必须是同步方法：<see cref="MediaLinkAudioFrame.TryDecode"/>
/// 的 <c>out ReadOnlySpan&lt;byte&gt;</c> 是 ref struct，不允许出现在 async 方法体内
/// （CS8175/CS9202）。拷贝在此处完成，异步路径只见 <c>byte[]</c>。
/// 这也是本类收 <see cref="IAudioFrameSubmitter"/> 而非异步汇的原因。
/// </summary>
public sealed class MediaLinkAudioReceiver
{
    private const long TicksPerSecond100Ns = 10_000_000;

    private readonly IAudioFrameSubmitter _submitter;
    private readonly Func<string?> _currentTrackToken;
    private readonly ILogger? _logger;
    private readonly Func<long> _nowQpc100Ns;

    public MediaLinkAudioReceiver(
        IAudioFrameSubmitter submitter,
        Func<string?> currentTrackTokenAccessor,
        ILogger? logger = null,
        Func<long>? nowQpc100NsProvider = null)
    {
        _submitter = submitter ?? throw new ArgumentNullException(nameof(submitter));
        _currentTrackToken = currentTrackTokenAccessor
            ?? throw new ArgumentNullException(nameof(currentTrackTokenAccessor));
        _logger = logger;
        _nowQpc100Ns = nowQpc100NsProvider ?? DefaultQpc100Ns;
    }

    public long AcceptedFrames { get; private set; }

    public long RejectedFrames { get; private set; }

    /// <summary>最近一次通过校验的帧头。三时间戳本期不消费（它们服务于抖动缓冲），仅供诊断。</summary>
    public MediaLinkAudioFrameHeader? LastHeader { get; private set; }

    /// <summary>
    /// <see cref="Stopwatch"/> 与 WASAPI 的 QPC 同源，但 <see cref="Stopwatch.Frequency"/>
    /// 不保证等于 10^7，故须显式归一到 100ns 而非假定两者刻度相同。
    /// </summary>
    private static long DefaultQpc100Ns() =>
        (long)(Stopwatch.GetTimestamp() * (double)TicksPerSecond100Ns / Stopwatch.Frequency);

    public MediaLinkAudioRejectReason Handle(ReadOnlySpan<byte> frame)
    {
        if (!MediaLinkAudioFrame.TryDecode(frame, out var header, out var pcm, out var error))
        {
            RejectedFrames++;
            // 丢帧不断连：未知 magic 可能是未来版本的其他二进制帧类型，
            // 按协议的「必须容忍未知」原则处理。
            _logger?.LogDebug("[音频] 丢弃无法解码的入站帧：{Error}", error);
            return MediaLinkAudioRejectReason.Malformed;
        }

        // 第 1 期挂账「PCM 奇数字节留给消费侧」——本处即那个消费侧。
        // i16 要求偶数字节，奇数说明上游编码有误。同样丢帧不断连：这是数据问题不是连接问题。
        if (pcm.Length % 2 != 0)
        {
            RejectedFrames++;
            _logger?.LogDebug("[音频] 丢弃 PCM 字节数为奇数的帧：{Length}", pcm.Length);
            return MediaLinkAudioRejectReason.OddPcmLength;
        }

        // 与歌词同源的既有规则：切歌后过期曲目的 PCM 直接丢弃。
        // 当前无曲目时也不接受——无从校验即不可信。
        var current = _currentTrackToken();
        if (string.IsNullOrEmpty(current) ||
            !string.Equals(current, header.TrackToken, StringComparison.Ordinal))
        {
            RejectedFrames++;
            return MediaLinkAudioRejectReason.TrackMismatch;
        }

        LastHeader = header;
        if (pcm.Length == 0)
        {
            // 帧本身合法，只是没有内容可分析。不计入接受数——那个计数用来回答
            // 「上游到底在不在发有效音频」，把空帧算进去会让它对这个问题说谎。
            return MediaLinkAudioRejectReason.None;
        }

        AcceptedFrames++;

        // QPC 填本地接收时刻，不用帧头的 capturedAtMs：后者是上游机器的时钟，
        // 跨机无意义。而下游只用它判断帧的新鲜度（本地语义），
        // 接收侧的「采样时刻」本就是本机对这块 PCM 的第一次观测。
        _submitter.Submit(new AudioFrame(
            pcm.ToArray(),
            _nowQpc100Ns(),
            MediaLinkProtocol.AudioSampleRate,
            MediaLinkProtocol.AudioChannels,
            header.Flags.HasFlag(MediaLinkAudioFrameFlags.Silent)));

        return MediaLinkAudioRejectReason.None;
    }
}
