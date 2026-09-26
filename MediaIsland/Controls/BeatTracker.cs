namespace MediaIsland.Controls;

/// <summary>
/// 在线节拍跟踪：四拍计数要数的是「拍」，不是「事件」。
///
/// 音乐里的事件（鼓击、咬字、换音）比拍密且不规律；只看单帧能量变化，分不出哪一下是拍。
/// 拍是周期量，只能从周期性里取：
///
/// 1. 包络：对数分频带能量的正向差，按真实时间重采样到固定 60Hz 网格——渲染表有抖动，
///    按「一次喂入 = 一帧」计的话，周期会随帧率漂移。
/// 2. 速度：包络近 3~8 秒的自相关，同时计入 2 倍周期（只奖励二拍子体系下自洽的周期），
///    乘以以 150 BPM 为中心的对数高斯先验。流行乐的快歌多在 170~190 BPM，先验放在 120
///    时会系统性地锁到 2:3 的错误层级上。换速带滞回，避免在两个候选之间来回跳。
/// 3. 相位：按当前周期做梳状打分找最近一拍，平滑地拉动下一拍的预测时刻。
/// 4. 报拍：到预测时刻即报拍。漏检的鼓击照样报拍，拍间的人声起伏也不会触发。
///
/// 自相关弱（没有明显节奏）或最近一秒几乎没有活动（暂停、静音）时不报拍——宁可不数，不要乱数。
/// 参数来自 14 首真实曲目（8 首留出验证）对照 madmom 参考节拍的离线评估。
/// </summary>
public sealed class BeatTracker
{
    private const double FramesPerSecond = 60;
    private const int WindowFrames = 480;
    private const int MinWindowFrames = 180;
    private const int EstimateEveryFrames = 15;
    private const double MinBpm = 60;
    private const double MaxBpm = 200;
    private const double PriorBpm = 150;
    private const double DoublePeriodWeight = 0.5;
    private const double SameTempoTolerance = 0.06;
    private const double SwitchMargin = 1.15;
    private const double PhaseBlend = 0.6;
    private const double MinConfidence = 0.05;

    /// <summary>最近这么多帧的平均活动不足窗口平均的这一比例，视为暂停或静音。</summary>
    private const int ActivityFrames = 60;
    private const double MinActivityRatio = 0.25;

    /// <summary>分频带：从第 1 个 bin 到 8kHz 对数等分 24 段。更高频几乎只有噪声与齿音。</summary>
    private const int BandCount = 24;
    private const double BandTopHz = 8000;

    /// <summary>对数压缩的增益：log(1 + C·E)。约 -60dB 以下近似线性归零，起到噪声门的作用。</summary>
    private const double Compression = 1000;

    private static readonly int MinLag = (int)(FramesPerSecond * 60 / MaxBpm);
    private static readonly int MaxLag = (int)Math.Ceiling(FramesPerSecond * 60 / MinBpm);

    private readonly double[] _envelope = new double[WindowFrames + 1];
    private readonly double[] _prior = BuildPrior();
    private readonly double[] _scores = new double[MaxLag - MinLag + 1];
    private readonly double[] _segment = new double[WindowFrames];
    private double[] _previousBands = [];
    private int[] _bandEdges = [];
    private int _edgesFor = -1;

    /// <summary>网格帧号（全局单调），与 _envelope 环形下标 = 帧号 % 长度。</summary>
    private long _frame = -1;
    private long _filled;
    private double _period;
    private double _nextBeat = double.NaN;
    private double _confidence;

    /// <summary>当前估计的速度；尚未估计时为 0。</summary>
    public double Bpm => _period > 0 ? FramesPerSecond * 60 / _period : 0;

    /// <summary>喂一帧谱。返回这一帧是否到了一拍。</summary>
    public bool Update(IReadOnlyList<float> spectrum, int sampleRate, double nowSeconds)
    {
        var value = Envelope(spectrum, sampleRate);
        var frame = (long)Math.Floor(nowSeconds * FramesPerSecond);
        if (_frame < 0 || frame - _frame > WindowFrames)
        {
            // 首帧或长时间中断：旧包络已全部滚出窗口，从头来过。
            Restart(frame);
        }

        if (frame <= _frame)
        {
            // 同一网格格内的第二次喂入：取大值，不推进时间。
            ref var slot = ref _envelope[Index(_frame)];
            slot = Math.Max(slot, value);
            return false;
        }

        var beat = false;
        while (_frame < frame)
        {
            _frame++;
            _filled++;
            _envelope[Index(_frame)] = _frame == frame ? value : 0;
            beat |= Step();
        }

        return beat;
    }

    public void Reset()
    {
        _previousBands = [];
        _frame = -1;
        _filled = 0;
        _period = 0;
        _nextBeat = double.NaN;
        _confidence = 0;
    }

    private void Restart(long frame)
    {
        Array.Clear(_envelope);
        _frame = frame - 1;
        _filled = 0;
        _period = 0;
        _nextBeat = double.NaN;
        _confidence = 0;
    }

    private int Index(long frame) => (int)(frame % _envelope.Length);

    /// <summary>推进一个网格帧。估计只用本帧之前的包络，与离线评估的因果口径一致。</summary>
    private bool Step()
    {
        var i = _frame;
        var window = (int)Math.Min(_filled - 1, WindowFrames);
        if (window >= MinWindowFrames && i % EstimateEveryFrames == 0)
        {
            Estimate(i, window);
        }

        if (double.IsNaN(_nextBeat) || i < _nextBeat)
        {
            return false;
        }

        var beat = _confidence >= MinConfidence && IsActive(i, window);
        _nextBeat += _period;
        while (_nextBeat <= i) _nextBeat += _period;
        return beat;
    }

    private void Estimate(long i, int window)
    {
        var mean = 0.0;
        for (var k = 0; k < window; k++) mean += _envelope[Index(i - window + k)];
        mean /= window;
        var energy = 0.0;
        for (var k = 0; k < window; k++)
        {
            var v = _envelope[Index(i - window + k)] - mean;
            _segment[k] = v;
            energy += v * v;
        }

        if (energy <= 1e-12)
        {
            _confidence = 0;
            return;
        }

        var best = 0;
        for (var k = 0; k < _scores.Length; k++)
        {
            var lag = MinLag + k;
            _scores[k] = (Autocorrelation(lag, window, energy)
                          + DoublePeriodWeight * Autocorrelation(2 * lag, window, energy)) * _prior[k];
            if (_scores[k] > _scores[best]) best = k;
        }

        _confidence = Autocorrelation(MinLag + best, window, energy);
        var candidate = RefinePeak(best);
        if (_period <= 0 || Math.Abs(candidate - _period) / _period < SameTempoTolerance)
        {
            _period = _period <= 0 ? candidate : _period + 0.5 * (candidate - _period);
        }
        else
        {
            var current = Math.Clamp((int)Math.Round(_period) - MinLag, 0, _scores.Length - 1);
            if (_scores[best] > SwitchMargin * _scores[current]) _period = candidate;
        }

        AlignPhase(i, window);
    }

    private double Autocorrelation(int lag, int window, double energy)
    {
        if (lag <= 0 || lag >= window) return 0;
        var sum = 0.0;
        for (var k = lag; k < window; k++) sum += _segment[k] * _segment[k - lag];
        return sum / energy;
    }

    /// <summary>抛物线插值把整数滞后细化到亚帧：60fps 下 1 帧的误差在 150 BPM 时就是 4% 的速度误差。</summary>
    private double RefinePeak(int k)
    {
        double lag = MinLag + k;
        if (k <= 0 || k >= _scores.Length - 1) return lag;
        var (a, b, c) = (_scores[k - 1], _scores[k], _scores[k + 1]);
        var curvature = a - 2 * b + c;
        return curvature < 0 ? lag + 0.5 * (a - c) / curvature : lag;
    }

    /// <summary>梳状打分找「最近一拍在几帧前」，再把下一拍的预测按比例拉过去。</summary>
    private void AlignPhase(long i, int window)
    {
        var period = _period;
        var teeth = (int)(window / period) - 1;
        var bestScore = -1.0;
        var bestOffset = 0;
        for (var offset = 0; offset < (int)Math.Ceiling(period); offset++)
        {
            var score = 0.0;
            for (var t = 0; t < teeth; t++)
            {
                var at = (long)Math.Round(i - 1 - offset - t * period);
                if (at < i - window) break;
                score += _envelope[Index(at)]
                         + 0.5 * _envelope[Index(Math.Max(i - window, at - 1))]
                         + 0.5 * _envelope[Index(Math.Min(i - 1, at + 1))];
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestOffset = offset;
            }
        }

        var target = i - 1 - bestOffset + period;
        while (target < i) target += period;
        if (double.IsNaN(_nextBeat))
        {
            _nextBeat = target;
            return;
        }

        var error = target - _nextBeat;
        error = ((error + period / 2) % period + period) % period - period / 2;
        _nextBeat += PhaseBlend * error;
    }

    /// <summary>最近一秒的平均活动相对整窗的比例。暂停后窗口里仍存着旧节奏，只看自相关会继续数下去。</summary>
    private bool IsActive(long i, int window)
    {
        if (window < ActivityFrames) return false;
        double recent = 0, all = 0;
        for (var k = 0; k < window; k++)
        {
            var v = _envelope[Index(i - window + k)];
            all += v;
            if (k >= window - ActivityFrames) recent += v;
        }

        return all > 0 && recent / ActivityFrames >= MinActivityRatio * all / window;
    }

    private double Envelope(IReadOnlyList<float> spectrum, int sampleRate)
    {
        if (spectrum.Count == 0)
        {
            _previousBands = [];
            return 0;
        }

        var edges = BandEdges(spectrum.Count, sampleRate);
        var bands = new double[edges.Length - 1];
        for (var b = 0; b < bands.Length; b++)
        {
            var energy = 0.0;
            for (var k = edges[b]; k < edges[b + 1]; k++) energy += spectrum[k];
            bands[b] = Math.Log(1 + Compression * energy);
        }

        var rise = 0.0;
        if (_previousBands.Length == bands.Length)
        {
            for (var b = 0; b < bands.Length; b++) rise += Math.Max(0, bands[b] - _previousBands[b]);
            rise /= bands.Length;
        }

        _previousBands = bands;
        return rise;
    }

    /// <summary>按实际谱长与采样率换算频带边界；相同输入只算一次。重复的整数边界去重，避免空频带。</summary>
    private int[] BandEdges(int binCount, int sampleRate)
    {
        var key = HashCode.Combine(binCount, sampleRate);
        if (key == _edgesFor && _bandEdges.Length > 0) return _bandEdges;

        var binHz = (sampleRate > 0 ? sampleRate : 48000) / (double)(binCount * 2);
        var top = Math.Clamp(BandTopHz / binHz, 2, binCount);
        var edges = new SortedSet<int>();
        for (var b = 0; b <= BandCount; b++)
        {
            edges.Add((int)Math.Min(binCount, Math.Pow(top, b / (double)BandCount)));
        }

        _bandEdges = edges.ToArray();
        _edgesFor = key;
        return _bandEdges;
    }

    private static double[] BuildPrior()
    {
        var prior = new double[MaxLag - MinLag + 1];
        var center = FramesPerSecond * 60 / PriorBpm;
        for (var k = 0; k < prior.Length; k++)
        {
            var octaves = Math.Log2((MinLag + k) / center);
            prior[k] = Math.Exp(-0.5 * octaves * octaves);
        }

        return prior;
    }
}
