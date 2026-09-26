namespace MediaIsland.Controls;

/// <summary>
/// 谱通量起拍检测。通量 = 各 bin 正向差之和；与自身约 1 秒的滑动均值比较，
/// 超过「均值 × 1.5 + 下限」且距上次起拍不少于 120ms 才判为起拍。
///
/// 自适应阈值而不给用户一个灵敏度滑块：安静的曲子与吵闹的曲子通量差一个数量级，
/// 固定阈值必然有一端失灵，而让用户调这个数是在把实现细节推给他们。
/// 每个组件各持一个实例——它有状态，且只有节拍涟漪与四拍计数用得到。
/// </summary>
public sealed class OnsetDetector
{
    private const double Ratio = 1.5;
    private const double Floor = 0.02;
    private const double MeanSeconds = 1.0;
    private const double MinIntervalSeconds = 0.12;

    private float[] _previous = [];
    private double _mean;
    private double _lastTime = double.NaN;
    private double _lastOnset = double.NegativeInfinity;

    /// <summary>喂一帧谱。返回这一帧是否为起拍。</summary>
    public bool Update(IReadOnlyList<float> spectrum, double nowSeconds)
    {
        var flux = 0.0;
        var count = Math.Min(spectrum.Count, _previous.Length);
        for (var i = 0; i < count; i++)
        {
            var rise = spectrum[i] - _previous[i];
            if (rise > 0)
            {
                flux += rise;
            }
        }

        // 第一帧没有「上一帧」，通量按 0 计——否则开播第一帧永远是起拍。
        flux = spectrum.Count == 0 ? 0 : flux / spectrum.Count;
        _previous = spectrum.ToArray();

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
}
