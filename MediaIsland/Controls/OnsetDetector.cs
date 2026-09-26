namespace MediaIsland.Controls;

/// <summary>
/// 鼓点起拍检测：只数宽频的上升。
///
/// 鼓击是宽频瞬态，在谱上是一条竖线；人声与合成器起音是窄带的，只占几个 bin。
/// 先沿频率取 33 点中值（±16 bin）平滑整条谱：窄于半窗的峰被当作离群值忽略，
/// 宽频击打则把中值整体抬起。再在平滑后的谱上做谱通量。
/// 不做这一步时换音、咬字都会触发——实测亮点只抓到约一半的鼓击，漏哪一下没有规律，
/// 看起来就是乱闪。曾试过「沿时间中值估谐波」的 HPSS 软掩码，但实时只能用过去的帧，
/// 新音刚出现时谐波估计还是底噪，掩码停在 0.5 附近拦不住它；真实曲目上也更差。
///
/// 通量 = 平滑谱各 bin 正向差之和 ÷ 平滑谱幅度总和；与自身约 1 秒的滑动均值比较，
/// 超过「均值 × 1.5 + 下限」且距上次起拍不少于 120ms 才判为起拍。除以幅度总和而非 bin 数：
/// 按 bin 数平均后通量约 1e-3，固定下限永不触发。相对通量与音量无关，
/// 下限才有确定含义：新增幅度至少占本帧总量的 10%。
///
/// 参数来自 14 首真实曲目（8 首留出验证）对照 madmom 参考节拍的离线评估。
/// 每个组件各持一个实例——它有状态。
/// </summary>
public sealed class OnsetDetector
{
    private const double Ratio = 1.5;
    private const double Floor = 0.1;

    /// <summary>幅度总和低于此值视为静音，通量按 0 计——近乎全零的谱上做除法，抖动噪声会被放大成起拍。</summary>
    private const double SilentTotal = 1e-3;
    private const double MeanSeconds = 1.0;
    private const double MinIntervalSeconds = 0.12;

    /// <summary>沿频率取中值的半宽（bin，48kHz 下约 375Hz）。窄于此宽度的峰视为音高而非打击。</summary>
    private const int BroadbandHalfWidth = 16;

    private readonly float[] _window = new float[BroadbandHalfWidth * 2 + 1];
    private float[] _previous = [];
    private double _mean;
    private double _lastTime = double.NaN;
    private double _lastOnset = double.NegativeInfinity;

    /// <summary>喂一帧谱。返回这一帧是否为起拍。</summary>
    public bool Update(IReadOnlyList<float> spectrum, double nowSeconds)
    {
        var broadband = Broadband(spectrum);

        var flux = 0.0;
        var total = 0.0;
        var count = Math.Min(broadband.Length, _previous.Length);
        for (var i = 0; i < count; i++)
        {
            total += broadband[i];
            var rise = broadband[i] - _previous[i];
            if (rise > 0)
            {
                flux += rise;
            }
        }

        // 第一帧没有「上一帧」，count 为 0，通量按 0 计——否则开播第一帧永远是起拍。
        flux = total < SilentTotal ? 0 : flux / total;
        _previous = broadband;

        var delta = double.IsNaN(_lastTime) ? 0 : Math.Max(0, nowSeconds - _lastTime);
        _lastTime = nowSeconds;

        var isOnset = flux > _mean * Ratio + Floor && nowSeconds - _lastOnset >= MinIntervalSeconds;
        if (isOnset)
        {
            _lastOnset = nowSeconds;
        }

        // 均值在判定之后更新：本帧的尖峰不该先抬高自己的门槛。
        var k = Math.Min(1, delta / MeanSeconds);
        _mean += (flux - _mean) * k;
        return isOnset;
    }

    public void Reset()
    {
        _previous = [];
        _mean = 0;
        _lastTime = double.NaN;
        _lastOnset = double.NegativeInfinity;
    }

    /// <summary>沿频率的滑动中值，边缘按最近值延拓。</summary>
    private float[] Broadband(IReadOnlyList<float> spectrum)
    {
        var n = spectrum.Count;
        var result = new float[n];
        for (var i = 0; i < n; i++)
        {
            for (var j = -BroadbandHalfWidth; j <= BroadbandHalfWidth; j++)
            {
                _window[j + BroadbandHalfWidth] = spectrum[Math.Clamp(i + j, 0, n - 1)];
            }

            Array.Sort(_window);
            result[i] = _window[BroadbandHalfWidth];
        }

        return result;
    }
}
