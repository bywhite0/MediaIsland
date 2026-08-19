using MediaIsland.Services.Audio;
using MediaIsland.Services.Audio.Native;
using Xunit;

namespace MediaIsland.Tests.Audio;

/// <summary>
/// native 采集不可用时的降级。
///
/// 这条路径必须成立：采集与播放依赖 native，而接收、转发、可视化全是纯托管的。
/// DLL 缺失或 ABI 不匹配只应让采集静默不可用，不得波及其余频道——
/// 这也让「只做接收端」在没有 native 库的平台上立即可用。
///
/// 沿用 TtmlNativeParser 的 IsAvailable / FailureReason 模式。
///
/// 注意：这些测试不触发真实采集（需要真机音频设备），只覆盖降级判定与
/// 生命周期的安全性。真实 loopback 由 AGENTS.md 的手工清单验证。
/// </summary>
[Collection(nameof(AudioNativeCollection))]
public class AudioCaptureNativeDegradationTests : IDisposable
{
    public AudioCaptureNativeDegradationTests() => AudioCaptureNative.ResetForTesting();

    public void Dispose() => AudioCaptureNative.ResetForTesting();

    [Fact]
    public void RealLibrary_ResolvesToExpectedAbiVersion()
    {
        // 构建产物真的被 MSBuild 复制到了输出目录，且 ABI 与托管侧的期望一致。
        // 这条一旦红，说明 csproj 的 native 复制或 Rust 侧的 ABI_VERSION 出了问题。
        AudioCaptureNative.EnsureResolved();

        Assert.True(
            AudioCaptureNative.IsAvailable,
            $"原生库应可用，实际失败原因：{AudioCaptureNative.FailureReason}");
        Assert.Null(AudioCaptureNative.FailureReason);
    }

    [Fact]
    public void SimulatedAbiMismatch_ReportsUnavailableWithReason()
    {
        AudioCaptureNative.ResetForTesting(available: false, failureReason: "ABI 版本不匹配：期望 3，实际为 99");

        Assert.False(AudioCaptureNative.IsAvailable);
        Assert.Contains("ABI", AudioCaptureNative.FailureReason);
    }

    [Fact]
    public void UnavailableSource_StartThrowsWithReason()
    {
        // 抛出而非静默：AudioFrameHub 负责把它降级为「不采集」，
        // 但帧源本身必须把失败原因说清楚，否则问题无从诊断。
        AudioCaptureNative.ResetForTesting(available: false, failureReason: "找不到 MediaIsland.Audio 原生库");
        using var source = new WasapiLoopbackFrameSource();

        var ex = Assert.Throws<InvalidOperationException>(
            () => source.StartAsync(CancellationToken.None).GetAwaiter().GetResult());

        Assert.Contains("找不到", ex.Message);
    }

    [Fact]
    public async Task UnavailableSource_ThroughHub_DegradesToNotCapturing()
    {
        // 这是真正要保的性质：宿主调 SetCaptureDemandAsync 不会因为 native 缺失而崩，
        // 只是安静地不采集。
        AudioCaptureNative.ResetForTesting(available: false, failureReason: "找不到 MediaIsland.Audio 原生库");
        using var source = new WasapiLoopbackFrameSource();
        using var hub = new AudioFrameHub(source);

        await hub.SetCaptureDemandAsync(true, CancellationToken.None);

        Assert.False(hub.IsCapturing);
        Assert.False(hub.IsSourceAvailable);
        Assert.Contains("找不到", hub.SourceFailureReason);
    }

    [Fact]
    public async Task StopWithoutStart_IsNoOp()
    {
        // 宿主停服路径会无条件调 stop，此时可能从未启动过。
        AudioCaptureNative.ResetForTesting(available: false);
        using var source = new WasapiLoopbackFrameSource();

        await source.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void DisposeTwice_IsHarmless()
    {
        AudioCaptureNative.ResetForTesting(available: false);
        var source = new WasapiLoopbackFrameSource();

        source.Dispose();
        source.Dispose();
    }

    [Fact]
    public async Task StopAfterDispose_DoesNotThrow()
    {
        // 释放顺序在宿主停服时不完全可控，撞上也不该炸。
        AudioCaptureNative.ResetForTesting(available: false);
        var source = new WasapiLoopbackFrameSource();
        source.Dispose();

        await source.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void StartAfterDispose_ThrowsObjectDisposed()
    {
        AudioCaptureNative.ResetForTesting(available: true);
        var source = new WasapiLoopbackFrameSource();
        source.Dispose();

        Assert.Throws<ObjectDisposedException>(
            () => source.StartAsync(CancellationToken.None).GetAwaiter().GetResult());
    }

    [Theory]
    [InlineData(AudioCaptureNative.StatusUnsupportedPlatform, "平台")]
    [InlineData(AudioCaptureNative.StatusDeviceError, "设备")]
    [InlineData(AudioCaptureNative.StatusAlreadyRunning, "运行")]
    [InlineData(AudioCaptureNative.StatusPanic, "原生库")]
    public void StatusCodes_MapToReadableChinese(int status, string expectedFragment)
    {
        // 状态码要能变成用户看得懂的话，否则设置页只能显示一个数字。
        Assert.Contains(expectedFragment, AudioCaptureNative.DescribeStatus(status));
    }

    [Fact]
    public void ReadLastError_NullHandle_ReturnsNull()
    {
        Assert.Null(AudioCaptureNative.ReadLastError(nint.Zero));
    }
}

/// <summary>
/// AudioCaptureNative 的探测结果是进程级静态状态，并发改写会互相干扰，故串行执行。
/// </summary>
[CollectionDefinition(nameof(AudioNativeCollection), DisableParallelization = true)]
public class AudioNativeCollection;
