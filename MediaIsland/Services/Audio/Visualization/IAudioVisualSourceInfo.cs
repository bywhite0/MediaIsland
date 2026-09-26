namespace MediaIsland.Services.Audio.Visualization;

/// <summary>
/// 组件判断「为什么没画面」所需的两个事实。做成接口是因为上游消费状态在 MediaLink 层，
/// 而 <c>Services/Audio</c> 不得引用 <c>Services/MediaLink</c>——实现放在 MediaLink 侧。
/// </summary>
public interface IAudioVisualSourceInfo
{
    bool IsLocalSourceAvailable { get; }

    string? LocalFailureReason { get; }

    bool IsConsumingUpstream { get; }
}
