using System.Buffers.Binary;
using MediaIsland.Services.Audio.Visualization;
using Xunit;

namespace MediaIsland.Tests.Audio;

/// <summary>
/// 环形缓冲对外只承诺三件事：混单声道的值对不对、最近一窗取得准不准、回绕后还准不准。
///
/// 断言一律用精确相等而非容差——测试值都取 32768 的整数分之一（如 8192 → 0.25），
/// i16 归一是除以 2 的幂，结果在 float 里可精确表示。用容差会放过真正的错误：
/// 比如把某个样本算成了相邻那个，差值远大于任何合理容差，但若容差订得松就看不出来。
/// 唯一的例外是 short.MaxValue，它天生落不到 1.0 上，理由见对应测试。
/// </summary>
public class AudioSampleRingTests
{
    /// <summary>i16 归一的除数。取 32768 而非 32767，理由见 <see cref="Int16Extremes_NormalizeToUnitRange"/>。</summary>
    private const float FullScale = 32768f;

    private static byte[] Pcm(params short[] samples)
    {
        var bytes = new byte[samples.Length * sizeof(short)];
        for (var i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * sizeof(short)), samples[i]);
        }

        return bytes;
    }

    /// <summary>值为 from..to 的连续 i16 序列。用递增值才能一眼看出取到的是哪一段。</summary>
    private static byte[] Ramp(int from, int to)
    {
        var samples = new short[to - from + 1];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)(from + i);
        }

        return Pcm(samples);
    }

    /// <summary><see cref="Ramp"/> 归一后的期望值。</summary>
    private static float[] Normalized(int from, int to)
    {
        var values = new float[to - from + 1];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = (from + i) / FullScale;
        }

        return values;
    }

    // ---- 构造与参数校验 ----

    [Fact]
    public void NonPowerOfTwoCapacity_Throws()
    {
        // 容量必须是 2 的幂，位置换算才能用掩码代替取模。若静默向上取整到 2 的幂，
        // 调用方按自己传的容量推算「能存多少毫秒」就会错，且错得没有任何信号。
        Assert.Throws<ArgumentException>(() => new AudioSampleRing(100));
        Assert.Throws<ArgumentException>(() => new AudioSampleRing(1));
        Assert.Throws<ArgumentException>(() => new AudioSampleRing(0));
        Assert.Throws<ArgumentException>(() => new AudioSampleRing(-8));
    }

    [Fact]
    public void PowerOfTwoCapacity_IsAcceptedAndExposed()
    {
        // 2 是下界，不能被「太小」的检查连带排除。
        Assert.Equal(2, new AudioSampleRing(2).Capacity);
        Assert.Equal(4096, new AudioSampleRing(4096).Capacity);
    }

    [Fact]
    public void NonPositiveChannels_Throws()
    {
        var ring = new AudioSampleRing(8);

        // 声道数会做除数，0 必须在算之前拦下——否则得到的是 DivideByZeroException
        // 或（浮点路径下）静默的 NaN，两者都比参数异常难查。
        Assert.Throws<ArgumentOutOfRangeException>(() => ring.Append(new byte[4], channels: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ring.Append(new byte[4], channels: -1));
    }

    [Fact]
    public void OddByteCount_Throws()
    {
        var ring = new AudioSampleRing(8);

        // i16 要求偶数字节。奇数说明上游切块切错了，继续算会让整段声道错位半个样本，
        // 表现为噪声而非静默——但那时已经离故障点很远了。
        Assert.Throws<ArgumentException>(() => ring.Append(new byte[3], channels: 1));
        Assert.Throws<ArgumentException>(() => ring.Append(new byte[1], channels: 1));
    }

    // ---- 读写顺序 ----

    [Fact]
    public void AppendedSamples_AreReadOldestFirst()
    {
        var ring = new AudioSampleRing(8);
        ring.Append(Pcm(8192, 16384, -8192, -16384), channels: 1);

        var destination = new float[4];

        Assert.True(ring.TryReadLatest(destination));
        // 顺序必须是写入顺序：FFT 与示波器都按时间轴解读这段数据，反向会让波形镜像。
        Assert.Equal([0.25f, 0.5f, -0.25f, -0.5f], destination);
    }

    [Fact]
    public void ReadingMoreThanWritten_ReturnsFalseAndLeavesDestinationUntouched()
    {
        var ring = new AudioSampleRing(8);
        ring.Append(Pcm(8192, 16384, -8192, -16384), channels: 1);

        var destination = new float[8];
        Array.Fill(destination, -7f);

        // 不足一窗时返回 false 而非补零：补零会在频谱上表现为一个真实存在的低频分量，
        // 调用方无从区分「真的静音」与「还没攒够」。哨兵值验证它连一个格子都没碰。
        Assert.False(ring.TryReadLatest(destination));
        Assert.All(destination, value => Assert.Equal(-7f, value));
    }

    [Fact]
    public void ReadingMoreThanCapacity_ReturnsFalse()
    {
        var ring = new AudioSampleRing(8);
        ring.Append(Ramp(1, 16), channels: 1);

        // 写够了但缓冲装不下——这与「还没攒够」是两回事，却同样只能拒绝。
        Assert.False(ring.TryReadLatest(new float[16]));
    }

    [Fact]
    public void AppendingBeyondCapacity_KeepsTheMostRecentWindow()
    {
        var ring = new AudioSampleRing(8);
        ring.Append(Ramp(1, 12), channels: 1);

        var destination = new float[8];

        Assert.True(ring.TryReadLatest(destination));
        // 溢出的是最旧的 4 个。可视化只关心「刚刚」，旧数据被覆盖是设计而非丢失。
        Assert.Equal(Normalized(5, 12), destination);
    }

    [Fact]
    public void ReadingAcrossTheWrapPoint_ReturnsContiguousLatestWindow()
    {
        var ring = new AudioSampleRing(8);
        // 分两次写，且第二次跨过回绕点：容量 8 写满 12 个，物理布局是 [9,10,11,12,5,6,7,8]，
        // 最近一窗在数组里是断开的。这条测试专门锁住拼接顺序——
        // 掩码算错时单次写入的用例仍会通过，只有跨回绕才暴露。
        ring.Append(Ramp(1, 6), channels: 1);
        ring.Append(Ramp(7, 12), channels: 1);

        var destination = new float[8];

        Assert.True(ring.TryReadLatest(destination));
        Assert.Equal(Normalized(5, 12), destination);
    }

    [Fact]
    public void ReadingLessThanAvailable_TakesTheNewestTail()
    {
        var ring = new AudioSampleRing(8);
        ring.Append(Ramp(1, 6), channels: 1);

        var destination = new float[3];

        Assert.True(ring.TryReadLatest(destination));
        // 取「最近 N 个」而非「最早 N 个」——分析器每次要的是当下这一窗。
        Assert.Equal(Normalized(4, 6), destination);
    }

    // ---- 混音与归一 ----

    [Fact]
    public void StereoFrames_AreMixedDownToMono()
    {
        var ring = new AudioSampleRing(4);
        // 第 1 帧左右反相，混后必须抵消为 0；第 2 帧左右同值，混后必须保持原值。
        // 两者合起来钉住的是「取平均」而非「取左声道」或「求和」：
        // 取左声道会让第 1 帧得 0.5，求和会让第 2 帧得 0.5。
        ring.Append(Pcm(16384, -16384, 8192, 8192), channels: 2);

        var destination = new float[2];

        Assert.True(ring.TryReadLatest(destination));
        Assert.Equal(0f, destination[0]);
        Assert.Equal(0.25f, destination[1]);
    }

    [Fact]
    public void Int16Extremes_NormalizeToUnitRange()
    {
        var ring = new AudioSampleRing(2);
        ring.Append(Pcm(short.MaxValue, short.MinValue), channels: 1);

        var destination = new float[2];

        Assert.True(ring.TryReadLatest(destination));
        // 除数取 32768（2^15）而非 32767：i16 的负半轴比正半轴多一格，
        // 用 32768 能让 MinValue 精确落在 -1.0，代价是 MaxValue 只到 0.99997。
        // 反过来用 32767 则 MinValue 会溢出到 -1.00003，把裁剪问题推给每个下游。
        // 少了 3e-5 对可视化不可察觉，而越界会真的画到控件外面。
        Assert.Equal(1f, destination[0], 1e-3);
        Assert.Equal(-1f, destination[1]);
    }

    // ---- 写入计数 ----

    [Fact]
    public void WrittenCount_TracksPerChannelFrameCount()
    {
        var ring = new AudioSampleRing(8);
        Assert.Equal(0L, ring.WrittenCount);

        // 计的是每声道帧数而非字节数或样本数：UI 侧靠它判断「有没有新数据」，
        // 而它要比较的对象是窗长（以采样计）。
        ring.Append(Pcm(1, 2, 3, 4), channels: 2);
        Assert.Equal(2L, ring.WrittenCount);

        ring.Append(Pcm(5, 6), channels: 1);
        Assert.Equal(4L, ring.WrittenCount);
    }

    [Fact]
    public void WrittenCount_KeepsGrowingPastCapacity()
    {
        var ring = new AudioSampleRing(8);
        ring.Append(Ramp(1, 12), channels: 1);

        // 单调递增，不随回绕归零——归零会让 UI 侧的「写位置变了吗」判断在回绕点误判为没变，
        // 频谱恰好在那一帧卡住。
        Assert.Equal(12L, ring.WrittenCount);
    }

    // ---- 空与残块 ----

    [Fact]
    public void EmptyAppend_IsNoOp()
    {
        var ring = new AudioSampleRing(8);
        ring.Append(Ramp(1, 4), channels: 1);

        ring.Append(ReadOnlySpan<byte>.Empty, channels: 2);

        // 空块不该推进写位置，否则 UI 侧会以为有新数据而白算一次 FFT。
        Assert.Equal(4L, ring.WrittenCount);
    }

    [Fact]
    public void PartialTrailingFrame_IsDropped()
    {
        var ring = new AudioSampleRing(8);
        // 3 个 i16 配 2 声道 = 1.5 帧。末尾那半帧没有右声道可混，只能丢。
        // 这条把静默截断变成显式约定：调用方若真送来半帧，行为是确定的，
        // 而不是某天变成「按左声道补齐」这类看起来更聪明的处理。
        ring.Append(Pcm(16384, 16384, 8192), channels: 2);

        Assert.Equal(1L, ring.WrittenCount);

        var destination = new float[1];
        Assert.True(ring.TryReadLatest(destination));
        Assert.Equal(0.5f, destination[0]);
    }

    [Fact]
    public void ReadingIntoEmptyDestination_SucceedsTrivially()
    {
        var ring = new AudioSampleRing(8);

        // 零长请求没有「不足」可言。返回 false 会让调用方把它当成缓冲未就绪而重试。
        Assert.True(ring.TryReadLatest(Span<float>.Empty));
    }
}
