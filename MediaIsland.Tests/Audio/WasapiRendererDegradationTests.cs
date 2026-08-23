using MediaIsland.Services.Audio.Playback.Native;
using Xunit;

namespace MediaIsland.Tests.Audio;

/// <summary>
/// 播放的降级路径与送帧判定。
///
/// native 缺失或 ABI 不匹配时播放不可用，而接收、转发、可视化都是纯托管的，必须照常工作——
/// 这是 Linux/macOS 上只做接收端与中继立即可用的全部依据。
///
/// 沿用采集侧的 IsAvailable / FailureReason 模式，故与
/// <see cref="AudioCaptureNativeDegradationTests"/> 共用同一个串行 collection。
///
/// 注意：这些测试不触发真实播放（需要真机音频设备），只覆盖降级判定、生命周期安全性
/// 与送帧的帧数换算。真实出声由 AGENTS.md 的手工清单验证。
/// </summary>
[Collection(nameof(AudioNativeCollection))]
public class WasapiRendererDegradationTests : IDisposable
{
    public WasapiRendererDegradationTests() => AudioRenderNative.ResetForTesting();

    public void Dispose() => AudioRenderNative.ResetForTesting();

    [Fact]
    public void RealLibrary_ResolvesToExpectedAbiVersion()
    {
        // 播放绑定有它自己的 ExpectedAbiVersion 常量，采集侧那条同名测试守不到它。
        // 这条一旦红，说明该常量与 Rust 侧的 ABI_VERSION 脱了钩。
        AudioRenderNative.EnsureResolved();

        Assert.True(
            AudioRenderNative.IsAvailable,
            $"原生库应可用，实际失败原因：{AudioRenderNative.FailureReason}");
        Assert.Null(AudioRenderNative.FailureReason);
    }

    [Fact]
    public void UnavailableNative_ReportsReasonInsteadOfThrowing()
    {
        AudioRenderNative.ResetForTesting(available: false, failureReason: "找不到 MediaIsland.Audio 原生库");

        using var renderer = new WasapiRenderer();

        Assert.False(renderer.IsAvailable);
        Assert.Equal("找不到 MediaIsland.Audio 原生库", renderer.FailureReason);
    }

    [Fact]
    public void StartOnUnavailableNative_Throws()
    {
        // 抛给调用方而非静默无视：AudioPlaybackService 据此回落直连。
        // 静默失败会让播放开关看起来生效了但没声音。
        AudioRenderNative.ResetForTesting(available: false, failureReason: "ABI 版本不匹配");
        using var renderer = new WasapiRenderer();

        var ex = Assert.Throws<InvalidOperationException>(() => renderer.Start(200));

        Assert.Contains("ABI", ex.Message);
    }

    [Fact]
    public void PushBeforeStart_IsIgnored()
    {
        AudioRenderNative.ResetForTesting(available: false, failureReason: "不可用");
        using var renderer = new WasapiRenderer();

        renderer.Push([0, 0, 0, 0], 0);   // 不抛即可
    }

    [Fact]
    public void StopWithoutStart_IsIdempotent()
    {
        AudioRenderNative.ResetForTesting(available: false);
        using var renderer = new WasapiRenderer();

        renderer.Stop();
        renderer.Stop();
    }

    [Fact]
    public void DisposeTwice_IsHarmless()
    {
        AudioRenderNative.ResetForTesting(available: false);
        var renderer = new WasapiRenderer();

        renderer.Dispose();
        renderer.Dispose();
    }

    [Fact]
    public void StartAfterDispose_ThrowsObjectDisposed()
    {
        AudioRenderNative.ResetForTesting(available: true);
        var renderer = new WasapiRenderer();
        renderer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => renderer.Start(200));
    }

    [Fact]
    public void PushAfterDispose_IsIgnored()
    {
        // 宿主停服时释放顺序不完全可控：网络收循环可能在 Dispose 之后再送一帧。
        // 此时句柄已销毁，送下去就是打在已释放内存上。
        AudioRenderNative.ResetForTesting(available: false);
        var renderer = new WasapiRenderer();
        renderer.Dispose();

        renderer.Push([0, 0, 0, 0], 0);
    }

    [Theory]
    [InlineData(AudioRenderNative.StatusUnsupportedPlatform, "平台")]
    [InlineData(AudioRenderNative.StatusDeviceError, "设备")]
    [InlineData(AudioRenderNative.StatusAlreadyRunning, "运行")]
    [InlineData(AudioRenderNative.StatusPanic, "原生库")]
    public void StatusCodes_MapToPlaybackWordingInChinese(int status, string expectedFragment)
    {
        var described = AudioRenderNative.DescribeStatus(status);

        Assert.Contains(expectedFragment, described);
        // 文案是从采集绑定照抄来的，漏改会让播放的故障显示成「采集…」。
        Assert.DoesNotContain("采集", described);
    }

    [Fact]
    public void ReadLastError_NullHandle_ReturnsNull()
    {
        Assert.Null(AudioRenderNative.ReadLastError(nint.Zero));
    }

    [Fact]
    public void FramesToPush_WhenNotRunning_IsZero()
    {
        // 未启动时句柄为零，送下去 native 只能返回 INVALID_ARG——而托管侧不看返回值，
        // 故这道门必须在托管侧就关上，且必须有判据。
        Assert.Equal((nuint)0, WasapiRenderer.FramesToPush(new byte[3840], running: false));
    }

    [Fact]
    public void FramesToPush_NullPayload_IsZero()
    {
        Assert.Equal((nuint)0, WasapiRenderer.FramesToPush(null, running: true));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, 0)]      // 不足一帧
    [InlineData(3, 0)]
    [InlineData(4, 1)]
    [InlineData(6, 1)]      // 模 4 余 2 的半帧被截断
    [InlineData(7, 1)]
    [InlineData(8, 2)]
    [InlineData(3840, 960)] // 20ms @ 48kHz 立体声
    public void FramesToPush_FloorsToWholeFrames(int byteCount, int expectedFrames)
    {
        // 传输侧只保证 PCM 字节数模 2（MediaLinkAudioReceiver 只拦奇数长度），不保证模 4。
        // native 的环形缓冲不跨调用结转半帧，故这里必须按整帧向下取整。
        Assert.Equal((nuint)expectedFrames, WasapiRenderer.FramesToPush(new byte[byteCount], running: true));
    }
}
