using System.Runtime.InteropServices;
using MediaIsland.Services.Audio.Playback;
using MediaIsland.Services.Audio.Playback.Native;
using Xunit;

namespace MediaIsland.Tests.Audio;

public class AudioRenderStatsTests
{
    [Fact]
    public void NativeLayout_MatchesRustRepr()
    {
        // 与 Rust 侧 render_stats_layout_has_no_padding 对称。两条都成立才说明两端一致。
        // 布局错位是静默的：读到的是别的字段的值，表现为「数值不对」，
        // 与「逻辑算错了」无从区分。
        Assert.Equal(112, Marshal.SizeOf<NativeRenderStats>());
    }

    [Theory]
    [InlineData(nameof(NativeRenderStats.RingFrames), 0)]
    [InlineData(nameof(NativeRenderStats.UnderrunCount), 8)]
    [InlineData(nameof(NativeRenderStats.HardResetCount), 16)]
    [InlineData(nameof(NativeRenderStats.DeviceFramesRendered), 24)]
    [InlineData(nameof(NativeRenderStats.DeviceSampleRate), 32)]
    [InlineData(nameof(NativeRenderStats.ResampleRatioPpm), 40)]
    [InlineData(nameof(NativeRenderStats.DevicePositionFrames), 48)]
    [InlineData(nameof(NativeRenderStats.DevicePositionQpc), 56)]
    [InlineData(nameof(NativeRenderStats.DeviceLatencyUs), 64)]
    [InlineData(nameof(NativeRenderStats.PlayTimeErrorUs), 72)]
    [InlineData(nameof(NativeRenderStats.TargetMsCurrent), 80)]
    [InlineData(nameof(NativeRenderStats.ClockOffsetAvailable), 88)]
    [InlineData(nameof(NativeRenderStats.DeviceBufferFrames), 96)]
    [InlineData(nameof(NativeRenderStats.DeviceClockAvailable), 104)]
    public void NativeLayout_PinsEveryFieldOffset(string field, int expectedOffset)
    {
        // 为什么 SizeOf 那一条不够：变异实测发现把首字段从 ulong 改成 uint 时
        // 总大小仍是 48（uint 后补 4 字节 padding 对齐到 8），SizeOf 判据全绿。
        // 而真正危险的错位是字段顺序错——它同样不改总大小，却让每个字段都读到
        // 别人的值，且在两端各自看都合法。只有逐字段偏移能钉住顺序。
        //
        // 偏移值来自 Rust 侧 RenderStats 的声明顺序，每个字段 8 字节宽、无中间 padding。
        // 其中 PlayTimeErrorUs 是 long（对 Rust 的 i64），宽度与其余一致，故不影响偏移。
        Assert.Equal(
            expectedOffset,
            (int)Marshal.OffsetOf<NativeRenderStats>(field));
    }

    [Theory]
    [InlineData(nameof(NativeRenderStats.PlayTimeErrorUs))]
    public void SignedFields_StaySigned(string field)
    {
        // 为什么偏移表与 SizeOf 都不够：变异实测把这个字段单侧改成 ulong 时两者全绿——
        // 宽度没变，偏移与总大小都不动，而值的解释已经反了。外环据误差的符号决定往哪个
        // 方向调，符号丢了方向就反，且回绕后的极大正值在诊断上看着像一次巨大的正误差。
        //
        // 这条断言的是声明本身，与偏移表同一性质：有些跨 FFI 的约定没有行为可观测，
        // 只能钉声明。它挡住的是「两侧各自改一半」这种形态。
        Assert.Equal(
            typeof(long),
            typeof(NativeRenderStats).GetField(field)!.FieldType);

        // 公开类型上同样是有符号，否则符号在这一层被丢掉。
        Assert.Equal(
            typeof(long),
            typeof(AudioRenderStats).GetProperty(field)!.PropertyType);
    }

    [Fact]
    public void NeverStarted_IsReportedAsSuch()
    {
        var stats = default(AudioRenderStats);

        Assert.False(stats.HasStarted);
        Assert.Equal(0, stats.RingMs);
        Assert.Equal(0, stats.RenderedMs);
    }

    [Fact]
    public void RingMs_UsesTheTransportRate_NotTheDeviceRate()
    {
        // RingFrames 是 48000Hz 域的量（环形缓冲存的是传输格式），拿设备率去换算
        // 会在非 48k 设备上把占用算错，而占用是漂移判据的核心量。
        // 44100 那个设备率放在这里是诱饵：它不该影响结果。
        var stats = new AudioRenderStats(
            RingFrames: 9_600,
            UnderrunCount: 0,
            HardResetCount: 0,
            DeviceFramesRendered: 0,
            DeviceSampleRate: 44_100,
            ResampleRatioPpm: 918_750);

        Assert.Equal(200, stats.RingMs, precision: 6);
    }

    [Fact]
    public void RenderedMs_UsesTheDeviceRate()
    {
        // 对称的另一半：已渲染帧数是设备帧，必须用设备率换算。
        var stats = new AudioRenderStats(
            RingFrames: 0,
            UnderrunCount: 0,
            HardResetCount: 0,
            DeviceFramesRendered: 44_100,
            DeviceSampleRate: 44_100,
            ResampleRatioPpm: 918_750);

        Assert.Equal(1_000, stats.RenderedMs, precision: 6);
    }

    [Fact]
    public void RenderedMs_IsZeroWhenDeviceRateIsUnknown()
    {
        // 除零防线。设备率为 0 时已渲染帧数必然也是 0，但判据不能依赖那个巧合。
        var stats = new AudioRenderStats(0, 0, 0, 480, 0, 0);

        Assert.Equal(0, stats.RenderedMs);
    }
}
