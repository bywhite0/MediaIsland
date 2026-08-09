namespace MediaIsland.Services.Audio.Visualization;

/// <summary>
/// 可视化的单一入口。两个帧源（本机采集经 <see cref="AudioFrameHub"/>、上游经
/// 协议接收侧）都提交到这里，组件也只认这里。
///
/// 本类不做源仲裁——「用本机还是上游」是连接态问题，而本层不知晓协议存在：
/// <c>Services/Audio</c> 不得引用 <c>Services/MediaLink</c>，反向才允许。
/// 仲裁在上游宿主服务侧完成，本层只管收到什么就分析什么。
/// </summary>
public sealed class AudioVisualizationService(
    AudioVisualizationDemand demand,
    AudioSpectrumAnalyzer analyzer) : IAudioFrameSink, IAudioFrameSubmitter
{
    public AudioVisualizationDemand Demand { get; } =
        demand ?? throw new ArgumentNullException(nameof(demand));

    public AudioSpectrumAnalyzer Analyzer { get; } =
        analyzer ?? throw new ArgumentNullException(nameof(analyzer));

    public ValueTask OnFrameAsync(AudioFrame frame, CancellationToken cancellationToken) =>
        Analyzer.OnFrameAsync(frame, cancellationToken);

    /// <summary>接收侧的同步入口。网络收循环不是 async 热路径，直接调用即可。</summary>
    public void Submit(AudioFrame frame) => Analyzer.Submit(frame);

    public AudioVisualizationSnapshot Capture() => Analyzer.Capture();
}
