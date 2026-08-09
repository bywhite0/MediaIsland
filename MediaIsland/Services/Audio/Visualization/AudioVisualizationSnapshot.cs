namespace MediaIsland.Services.Audio.Visualization;

/// <summary>
/// 一次分析的完整结果。复合而非只有频谱，因为不是所有形态都需要 FFT：
/// 示波器要时域抽样、电平表要 RMS 与峰值、律动条要限频段能量归约的标量。
/// 渲染层按当前形态各取所需，故切换形态是纯渲染层的事，分析层不变。
///
/// 装的是定长原始幅度谱，不是分好的频段。频段映射（段数、频率范围）是
/// 每个组件各自的显示口味，而 FFT 是所有组件共享的昂贵计算——两者分属不同侧。
/// 若把频段映射放进分析层，单例分析器会让 A 组件改段数时 B 组件跟着变。
/// 这与 attack/decay 留在组件侧是同一条分界线：
/// 贵且与观察者无关的共享，廉价且属于观察者的各自持有。
/// </summary>
public sealed record AudioVisualizationSnapshot
{
    /// <summary>
    /// 无数据时的快照。<see cref="Revision"/> 为 0，而实算出来的快照从 1 起算，
    /// 故渲染层可用「Revision 大于 0」区分「真的算过」与「还没攒够一窗」。
    /// </summary>
    public static readonly AudioVisualizationSnapshot Empty = new()
    {
        Spectrum = [],
        Waveform = [],
        Rms = 0f,
        Peak = 0f,
        IsSilent = true,
        Revision = 0
    };

    /// <summary>原始幅度谱，长度恒为 <see cref="AudioSpectrumAnalyzer.SpectrumBinCount"/>。</summary>
    public required IReadOnlyList<float> Spectrum { get; init; }

    /// <summary>时域抽样，值域 -1..1。不经平滑——它是波形本身，平滑会把它抹平。</summary>
    public required IReadOnlyList<float> Waveform { get; init; }

    public required float Rms { get; init; }

    public required float Peak { get; init; }

    public required bool IsSilent { get; init; }

    /// <summary>单调递增。未变说明未重算，渲染层可据此跳过刷新目标值——但不可据此跳过整帧，
    /// 否则上游断连后最后一帧频谱会永远停在屏幕上。</summary>
    public required long Revision { get; init; }
}
