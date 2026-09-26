namespace MediaIsland.Controls;

/// <summary>
/// 从共享幅度谱推出的廉价特征。放在组件侧而非分析层——与频段映射、包络同一条分界：
/// 贵且与观察者无关的共享，廉价且属于观察者的各自持有。
/// 所有方法都不假设频率区间有序：用户可以把下限拖到上限之上。
/// </summary>
public static class AudioFeatures
{
    /// <summary>色度折叠的下限。约 23Hz 的 bin 宽在 110Hz 以下已分不清相邻半音。</summary>
    public const double ChromaMinHz = 110;

    /// <summary>
    /// 频谱质心，按对数频率归一到 0..1。对数轴与频段映射一致——人耳对「亮」的感知接近对数。
    /// 全零返回 0.5（中性），不返回 NaN：静音时颜色不该跳到某一端。
    /// </summary>
    public static float SpectralCentroid(IReadOnlyList<float> spectrum, int sampleRate, double minHz, double maxHz)
    {
        var (lo, hi) = Order(minHz, maxHz);
        if (spectrum.Count == 0 || sampleRate <= 0)
        {
            return 0.5f;
        }

        var binHz = sampleRate / (spectrum.Count * 2.0);
        double weighted = 0, total = 0;
        for (var i = 1; i < spectrum.Count; i++)
        {
            var hz = i * binHz;
            if (hz < lo || hz > hi || !(spectrum[i] > 0))
            {
                continue;
            }

            weighted += spectrum[i] * Math.Log(hz / lo);
            total += spectrum[i];
        }

        if (total <= 0)
        {
            return 0.5f;
        }

        return (float)Math.Clamp(weighted / total / Math.Log(hi / lo), 0, 1);
    }

    /// <summary>
    /// 十二半音能量，下标 0 = C。以 A4 = 440Hz 为基准；只取 ≥ <see cref="ChromaMinHz"/> 且在区间内的 bin。
    /// 最大值归一到 1，让组件只管相对亮度——绝对量已由包络与能量样式负责。
    /// </summary>
    public static float[] ChromaFold(IReadOnlyList<float> spectrum, int sampleRate, double minHz, double maxHz)
    {
        var chroma = new float[12];
        var (lo, hi) = Order(minHz, maxHz);
        lo = Math.Max(lo, ChromaMinHz);
        if (spectrum.Count == 0 || sampleRate <= 0 || lo >= hi)
        {
            return chroma;
        }

        var binHz = sampleRate / (spectrum.Count * 2.0);
        for (var i = 1; i < spectrum.Count; i++)
        {
            var hz = i * binHz;
            if (hz < lo || hz > hi || !(spectrum[i] > 0))
            {
                continue;
            }

            // 相对 A 的半音数，再平移到以 C 为 0：A 在 C 之上 9 个半音。
            var semitone = (int)Math.Round(12 * Math.Log2(hz / 440.0)) + 9;
            chroma[((semitone % 12) + 12) % 12] += spectrum[i];
        }

        var max = chroma.Max();
        if (max > 0)
        {
            for (var k = 0; k < 12; k++)
            {
                chroma[k] /= max;
            }
        }

        return chroma;
    }

    /// <summary>归一区间：下限不为正时抬到 1Hz，下限不小于上限时交换或撑开 1Hz。</summary>
    internal static (double Lo, double Hi) Order(double a, double b)
    {
        var lo = Math.Max(1, Math.Min(a, b));
        var hi = Math.Max(a, b);
        return lo >= hi ? (lo, lo + 1) : (lo, hi);
    }
}
