namespace MediaIsland.Services.Audio.Visualization.Fft;

/// <summary>
/// 就地 radix-2 Cooley-Tukey FFT。
///
/// 手写而不引 MathNet.Numerics：本仓只有 ClassIsland.PluginSdk 与 Sentry 两个包，
/// 为一个约 60 行的确定性算法引入数 MB 依赖，在插件分发体积上不划算，
/// 且多一个版本冲突面。手写另有一条实质好处——它是纯函数，
/// 可用已知正弦波直接断言峰值位置，不必先弄清第三方库的归一化约定。
/// </summary>
public static class RealFft
{
    /// <summary>
    /// 就地变换：<paramref name="real"/> 与 <paramref name="imaginary"/> 同时被结果覆写，
    /// 纯实信号把虚部传全零即可。零分配、不碰共享状态，可每帧调用。
    ///
    /// 结果**不做归一化**，量级随长度线性增长（全 1.0 的 N 点输入，bin 0 == N）。
    /// 归一化留给调用方：加窗与否、显示如何标定各有不同系数，塞进这里会绑死一种策略。
    /// </summary>
    /// <exception cref="ArgumentException">
    /// 长度不是 2 的幂或小于 2（radix-2 分解的前提），或两个 span 长度不同。
    /// </exception>
    public static void Transform(Span<float> real, Span<float> imaginary)
    {
        var n = real.Length;
        EnsureFftLength(n, nameof(real));
        EnsureSameLength(n, imaginary.Length, nameof(imaginary));

        // 位反转置换：把蝶形运算需要的下标顺序一次排好，之后逐级原地合并。
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;
            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imaginary[i], imaginary[j]) = (imaginary[j], imaginary[i]);
            }
        }

        for (var len = 2; len <= n; len <<= 1)
        {
            var angle = -2.0 * Math.PI / len;
            var wReal = (float)Math.Cos(angle);
            var wImaginary = (float)Math.Sin(angle);

            for (var start = 0; start < n; start += len)
            {
                float curReal = 1f, curImaginary = 0f;
                for (var k = 0; k < len / 2; k++)
                {
                    var evenIndex = start + k;
                    var oddIndex = evenIndex + len / 2;

                    var oddReal = real[oddIndex] * curReal - imaginary[oddIndex] * curImaginary;
                    var oddImaginary = real[oddIndex] * curImaginary + imaginary[oddIndex] * curReal;

                    real[oddIndex] = real[evenIndex] - oddReal;
                    imaginary[oddIndex] = imaginary[evenIndex] - oddImaginary;
                    real[evenIndex] += oddReal;
                    imaginary[evenIndex] += oddImaginary;

                    var nextReal = curReal * wReal - curImaginary * wImaginary;
                    curImaginary = curReal * wImaginary + curImaginary * wReal;
                    curReal = nextReal;
                }
            }
        }
    }

    /// <summary>
    /// 由 <see cref="Transform"/> 的输出算幅度谱，写入 <paramref name="magnitudes"/>。
    ///
    /// 只输出前 N/2 个 bin：实信号的谱共轭对称，后半是镜像。下标 i 对应
    /// <c>i * sampleRate / N</c> Hz，**不含** Nyquist bin，即频率轴是半开区间 [0, sampleRate/2)。
    /// 标度承接 <see cref="Transform"/>，同样未归一化。
    /// </summary>
    /// <exception cref="ArgumentException">
    /// 长度校验与 <see cref="Transform"/> 同源——只接受它能产出的长度，
    /// 否则调用方无法从一个方法的行为推断另一个。<paramref name="magnitudes"/> 长度须恰为 N/2。
    /// </exception>
    public static void Magnitudes(
        ReadOnlySpan<float> real, ReadOnlySpan<float> imaginary, Span<float> magnitudes)
    {
        EnsureFftLength(real.Length, nameof(real));
        EnsureSameLength(real.Length, imaginary.Length, nameof(imaginary));

        if (magnitudes.Length != real.Length / 2)
        {
            throw new ArgumentException(
                "幅度谱长度必须是 FFT 长度的一半（实信号的谱共轭对称，后半无信息）",
                nameof(magnitudes));
        }

        for (var i = 0; i < magnitudes.Length; i++)
        {
            magnitudes[i] = MathF.Sqrt(real[i] * real[i] + imaginary[i] * imaginary[i]);
        }
    }

    /// <summary>
    /// Hann 窗。不加窗会让非整周期信号的谱泄漏到相邻 bin，频谱柱看起来整片抬升。
    /// </summary>
    public static void ApplyHannWindow(Span<float> samples)
    {
        var n = samples.Length;
        if (n < 2)
        {
            return;
        }

        for (var i = 0; i < n; i++)
        {
            samples[i] *= 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / (n - 1)));
        }
    }

    private static void EnsureFftLength(int length, string parameterName)
    {
        if (length < 2 || (length & (length - 1)) != 0)
        {
            throw new ArgumentException($"FFT 长度必须是 2 的幂且不小于 2，实际 {length}", parameterName);
        }
    }

    private static void EnsureSameLength(int expected, int actual, string parameterName)
    {
        if (actual != expected)
        {
            throw new ArgumentException($"实部与虚部长度必须相同，实际 {actual} 对 {expected}", parameterName);
        }
    }
}
