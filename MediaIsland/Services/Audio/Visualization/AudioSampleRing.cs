using System.Buffers.Binary;

namespace MediaIsland.Services.Audio.Visualization;

/// <summary>
/// 单写单读的环形缓冲，存混单声道后的 float 采样。
///
/// 采集帧约 480 采样/声道（实测 10ms），而 FFT 需 2048 点——**不存在「一帧一次 FFT」**，
/// 必须累积。容量取 2 窗，保证任何时刻都能凑出一整窗。
///
/// 写者是音频线程（唯一写者），读者是 UI 线程。写位置用 <see cref="Volatile"/> 读写，
/// **不加锁**：极端情况下读到跨越写指针的撕裂数据，后果是一帧频谱有噪声、下一帧即恢复，
/// 不值得为它阻塞音频线程——阻塞会让 WASAPI 缓冲溢出并真的丢帧。
/// </summary>
public sealed class AudioSampleRing
{
    /// <summary>
    /// i16 归一的除数。取 2^15 而非 <see cref="short.MaxValue"/>：i16 的负半轴比正半轴多一格，
    /// 除以 32768 能让 <see cref="short.MinValue"/> 精确落在 -1.0，代价是正端只到 0.99997。
    /// 反过来除以 32767 则负端会溢出到 -1.00003，把裁剪问题推给每一个下游。
    /// 少的那 3e-5 对可视化不可察觉，而越界会真的画到控件外面。
    /// </summary>
    private const float FullScale = 32768f;

    private readonly float[] _buffer;
    private readonly int _mask;
    private long _written;

    public AudioSampleRing(int capacity)
    {
        // 必须是 2 的幂：位置换算才能用掩码代替取模，而取模在音频线程的逐样本循环里不便宜。
        // 不向上取整到 2 的幂，因为调用方会按自己传的容量推算「能存多少毫秒」。
        if (capacity < 2 || (capacity & (capacity - 1)) != 0)
        {
            throw new ArgumentException($"容量必须是 2 的幂且不小于 2，实际 {capacity}", nameof(capacity));
        }

        _buffer = new float[capacity];
        _mask = capacity - 1;
    }

    public int Capacity => _buffer.Length;

    /// <summary>
    /// 已写入的总采样数（每声道），单调递增。UI 侧据此判断有没有新数据。
    /// **不随回绕归零**——归零会让「写位置变了吗」的判断在回绕点误判为没变，频谱恰好卡那一帧。
    /// </summary>
    public long WrittenCount => Volatile.Read(ref _written);

    /// <summary>
    /// i16 交错 PCM 混为单声道 float(-1..1) 后追加。音频线程调用。
    ///
    /// 末尾不完整的帧（样本数不是声道数的整数倍）直接丢弃：那半帧没有右声道可混，
    /// 补齐要凭空造数据。溢出容量的旧数据被覆盖是设计而非丢失，可视化只关心「刚刚」。
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="channels"/> 不为正。</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="interleavedPcm"/> 字节数为奇数——i16 要求偶数字节，
    /// 奇数说明上游切块切错了，继续算会让整段声道错位半个样本。
    /// </exception>
    public void Append(ReadOnlySpan<byte> interleavedPcm, int channels)
    {
        // 声道数会做除数，必须在算之前拦下：否则得到的是除零异常或静默的 NaN，都比参数异常难查。
        if (channels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channels), channels, "声道数必须为正");
        }

        if (interleavedPcm.Length % 2 != 0)
        {
            throw new ArgumentException(
                $"i16 PCM 的字节数必须为偶数，实际 {interleavedPcm.Length}", nameof(interleavedPcm));
        }

        var totalSamples = interleavedPcm.Length / sizeof(short);
        var frames = totalSamples / channels;
        var written = Volatile.Read(ref _written);

        for (var f = 0; f < frames; f++)
        {
            var sum = 0f;
            for (var c = 0; c < channels; c++)
            {
                var sample = BinaryPrimitives.ReadInt16LittleEndian(
                    interleavedPcm[((f * channels + c) * sizeof(short))..]);
                sum += sample / FullScale;
            }

            _buffer[(int)((written + f) & _mask)] = sum / channels;
        }

        // 数据先落，位置后发布：读者看到新位置时对应槽位一定已写好。
        Volatile.Write(ref _written, written + frames);
    }

    /// <summary>
    /// 取最近 <c>destination.Length</c> 个采样，最旧在前。不足则返回 false 且**不写**
    /// <paramref name="destination"/>——补零会在频谱上表现为一个真实存在的低频分量，
    /// 调用方无从区分「真的静音」与「还没攒够」。
    /// </summary>
    public bool TryReadLatest(Span<float> destination)
    {
        var written = Volatile.Read(ref _written);
        var want = destination.Length;
        if (want > _buffer.Length || written < want)
        {
            return false;
        }

        var start = written - want;
        for (var i = 0; i < want; i++)
        {
            destination[i] = _buffer[(int)((start + i) & _mask)];
        }

        return true;
    }
}
