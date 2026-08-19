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
        Assert.Equal(48, Marshal.SizeOf<NativeRenderStats>());
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
