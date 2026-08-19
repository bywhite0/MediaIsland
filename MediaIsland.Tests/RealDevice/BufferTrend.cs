namespace MediaIsland.Tests.RealDevice;

/// <summary>
/// 缓冲占用的趋势判定。
///
/// 判斜率而不判「有没有破音」：破音是漂移积累到缓冲走空或走满之后的结果，
/// 而那需要十分钟量级的等待——那个时长是人耳判据的产物，人只能等到破音才知道。
/// 占用可读时，走向空或满在几十秒内就表现为单调趋势。
/// </summary>
internal static class BufferTrend
{
    /// <summary>
    /// 最小二乘斜率，单位帧每秒。
    ///
    /// 样本不足两点、或时间轴零方差时返回 0 而不抛：真机测试里采样可能因设备故障
    /// 而拿不到，那时该由调用方的「样本数」判据报错，不该由这里抛异常把原因盖掉。
    /// 返回 0 而不是 NaN 也是刻意的——NaN 会静默通过 Math.Abs(x) 小于阈值这类判据。
    /// </summary>
    public static double SlopeFramesPerSecond(IReadOnlyList<(double Seconds, long Frames)> samples)
    {
        if (samples.Count < 2)
        {
            return 0;
        }

        double meanX = 0;
        double meanY = 0;
        foreach (var (seconds, frames) in samples)
        {
            meanX += seconds;
            meanY += frames;
        }

        meanX /= samples.Count;
        meanY /= samples.Count;

        double covariance = 0;
        double variance = 0;
        foreach (var (seconds, frames) in samples)
        {
            var dx = seconds - meanX;
            covariance += dx * (frames - meanY);
            variance += dx * dx;
        }

        return variance <= 0 ? 0 : covariance / variance;
    }
}
