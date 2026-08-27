namespace MediaIsland.Tests.Audio;

/// <summary>
/// 真机音频判据共用的振幅阈值，全仓唯一定义处。
/// 对齐类判据必须先断言确实有声音，再断言时间性质——静音端点上时刻误差恒为零，
/// 缺振幅下界的判据在全零采样下会假绿。
/// 阈值只许在这里定义一次：两份各自为真的副本就是下一次漂移的起点。
/// </summary>
internal static class AudioAlignmentThresholds
{
    /// <summary>
    /// 采回峰值振幅的下界，单位是 16 位样本的绝对值。真机实测：注入振幅 16384 时
    /// 24 位路径往返采回 16383（差一个最低位）；44.1kHz 到 48kHz 重采样路径采回
    /// 16545（sinc 过冲约 1%）。取 1000（约 -30 dBFS）——比实测低 16 倍以上，
    /// 不会因重采样或位深往返误红；又远高于全零与最低位量级的抖动，不会漏过
    /// 静音端点的假绿。
    /// </summary>
    public const int PeakAmplitudeLowerBound = 1000;

    /// <summary>
    /// 含非零样本的帧占收到总帧数之比的下界。静音端点上该占比恒为 0；
    /// 正常放音的真机实测接近 1。取 0.5：对正常放音留一倍裕度，容忍半个采样窗
    /// 的换曲间隙或淡入；静音端点则无论窗口多长都拿不到任何正值。
    /// </summary>
    public const double NonZeroFrameRatioLowerBound = 0.5;
}
