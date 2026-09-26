using MediaIsland.Services.Audio;
using MediaIsland.Services.Audio.Visualization;

namespace MediaIsland.Services.MediaLink;

/// <summary>
/// 把本机采集与上游消费两个事实拼给组件。上游状态用委托延迟读取：
/// 上游服务的工厂会即时解析服务端，这里直接注入它会把组件拖进那张图的构造顺序里。
/// </summary>
public sealed class MediaLinkAudioVisualSourceInfo(LocalAudioCapture capture, Func<bool> consumingUpstream)
    : IAudioVisualSourceInfo
{
    public bool IsLocalSourceAvailable => capture.IsSourceAvailable;

    public string? LocalFailureReason => capture.SourceFailureReason;

    public bool IsConsumingUpstream => consumingUpstream();
}
