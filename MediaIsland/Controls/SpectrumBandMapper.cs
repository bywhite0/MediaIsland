namespace MediaIsland.Controls;

/// <summary>
/// 定长幅度谱 → 显示用频段。
///
/// 在组件侧而非分析层：段数与频率范围是每个组件各自的显示口味，
/// 而 FFT 是所有组件共享的昂贵计算。分析器是单例，把映射放进去会让
/// 一个组件改段数时另一个跟着变。分界线与 attack/decay 相同——
/// 贵且与观察者无关的共享，廉价且属于观察者的各自持有。
///
/// 频段按对数分布：人耳对频率的感知接近对数，线性分段会把大半个可视宽度
/// 让给听感上区分不出的高频，低频挤成一根。
/// </summary>
public static class SpectrumBandMapper
{
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bandCount"/> 不为正。</exception>
    /// <exception cref="ArgumentException">频率范围无效（下限不为正，或下限不小于上限）。</exception>
    public static float[] Map(
        IReadOnlyList<float> spectrum, int sampleRate, int bandCount,
        double minFrequencyHz, double maxFrequencyHz)
    {
        if (bandCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bandCount), bandCount, "频段数必须为正");
        }

        ValidateRange(minFrequencyHz, maxFrequencyHz);

        var bands = new float[bandCount];
        if (spectrum.Count == 0)
        {
            // 分析器攒够一窗之前返回空谱，这是启动后的常态而非异常。
            return bands;
        }

        var binHz = BinWidthHz(spectrum.Count, sampleRate);
        var logMin = Math.Log(minFrequencyHz);
        var logMax = Math.Log(maxFrequencyHz);

        for (var b = 0; b < bandCount; b++)
        {
            var lowHz = Math.Exp(logMin + (logMax - logMin) * b / bandCount);
            var highHz = Math.Exp(logMin + (logMax - logMin) * (b + 1) / bandCount);
            bands[b] = PeakBetween(spectrum, binHz, lowHz, highHz);
        }

        return bands;
    }

    /// <summary>限频段能量归约为标量，供律动条使用。</summary>
    /// <exception cref="ArgumentException">频率范围无效。校验与 <see cref="Map"/> 同源。</exception>
    public static float Energy(
        IReadOnlyList<float> spectrum, int sampleRate,
        double minFrequencyHz, double maxFrequencyHz)
    {
        ValidateRange(minFrequencyHz, maxFrequencyHz);
        if (spectrum.Count == 0)
        {
            return 0f;
        }

        return PeakBetween(spectrum, BinWidthHz(spectrum.Count, sampleRate), minFrequencyHz, maxFrequencyHz);
    }

    /// <summary>
    /// 谱长是 FFT 长度的一半（实信号的谱共轭对称），故 bin 宽度 = sampleRate / (count * 2)。
    /// 按实际谱长反算而不写死 2048：日后改帧长时，写死的那个会让整条频率轴静默偏移。
    /// </summary>
    private static double BinWidthHz(int spectrumCount, int sampleRate) =>
        sampleRate / (double)(spectrumCount * 2);

    private static void ValidateRange(double minFrequencyHz, double maxFrequencyHz)
    {
        // 下限必须为正：取对数要用它，0 会得到负无穷，负数会得到 NaN，
        // 而 NaN 参与比较永远为假，表现是整片频谱静默变全零。
        if (minFrequencyHz <= 0 || minFrequencyHz >= maxFrequencyHz)
        {
            throw new ArgumentException(
                $"频率范围无效：{minFrequencyHz}–{maxFrequencyHz}", nameof(minFrequencyHz));
        }
    }

    /// <summary>
    /// 区间内取最大值而非平均：平均会被区间内的空白 bin 稀释，视觉上偏闷——
    /// 高频段动辄跨几十个 bin，一个真实的峰会被摊平。
    /// 区间完全落在谱之外时返回 0，不抛：用户可以把范围调到任何地方。
    /// </summary>
    private static float PeakBetween(
        IReadOnlyList<float> spectrum, double binHz, double lowHz, double highHz)
    {
        var lowBin = (int)(lowHz / binHz);
        var highBin = (int)Math.Ceiling(highHz / binHz);
        if (lowBin >= spectrum.Count || highBin <= 0)
        {
            return 0f;
        }

        lowBin = Math.Max(lowBin, 0);
        // 至少取一个 bin：段比 bin 密时 lowBin 与 highBin 会算成同一个，
        // 空区间会让那些段恒为零而不是重复取同一个 bin。
        highBin = Math.Min(Math.Max(highBin, lowBin + 1), spectrum.Count);

        var maximum = 0f;
        for (var i = lowBin; i < highBin; i++)
        {
            if (spectrum[i] > maximum)
            {
                maximum = spectrum[i];
            }
        }

        return maximum;
    }
}
