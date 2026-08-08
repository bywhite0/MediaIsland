using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace MediaIsland.Services.Audio.Native;

/// <summary>
/// 基于 native WASAPI loopback 的帧源。
///
/// 回调走 <c>[UnmanagedCallersOnly]</c> 静态函数 + <see cref="GCHandle"/>：实例方法无法直接
/// 当函数指针交给 native，而闭包委托会被 GC 移动或回收——那会表现为随机时刻的
/// AccessViolation，且崩溃点与真正的错误相距甚远。静态入口 + 固定句柄是唯一稳定的形态。
///
/// PCM 必须在回调内拷出：native 缓冲返回即失效，留着指针晚点读就是读已释放内存。
/// 这与第 1 期 <c>MediaLinkAudioFrame.TryDecode</c> 的零拷贝 span 是同一类约束的第二次出现。
/// </summary>
internal sealed class WasapiLoopbackFrameSource : IAudioFrameSource, IDisposable
{
    private readonly ILogger? _logger;
    private readonly object _gate = new();

    private GCHandle _selfHandle;
    private nint _handle;
    private bool _running;
    private bool _disposed;

    public WasapiLoopbackFrameSource(ILogger? logger = null)
    {
        _logger = logger;
        AudioCaptureNative.EnsureResolved(logger);
    }

    public event Action<AudioFrame>? FrameAvailable;

    public bool IsAvailable => AudioCaptureNative.IsAvailable;

    public string? FailureReason => AudioCaptureNative.FailureReason;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_running)
            {
                return Task.CompletedTask;
            }

            if (!AudioCaptureNative.IsAvailable)
            {
                throw new InvalidOperationException(
                    $"音频采集不可用：{AudioCaptureNative.FailureReason ?? "原因未知"}");
            }

            EnsureHandleCreated();

            var status = AudioCaptureNative.NativeMethods.CaptureStart(_handle);
            if (status != AudioCaptureNative.StatusOk)
            {
                var detail = AudioCaptureNative.ReadLastError(_handle)
                             ?? AudioCaptureNative.DescribeStatus(status);
                throw new InvalidOperationException($"启动音频采集失败：{detail}");
            }

            _running = true;
            _logger?.LogInformation("[音频:采集] 已启动 WASAPI loopback 采集。");
            return Task.CompletedTask;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_running || _handle == nint.Zero)
            {
                return Task.CompletedTask;
            }

            // native 的 stop 同步等采集线程退出：返回后回调不可能再触发，
            // 此时才敢让托管侧继续走向 GCHandle 释放。
            var status = AudioCaptureNative.NativeMethods.CaptureStop(_handle);
            _running = false;

            if (status != AudioCaptureNative.StatusOk)
            {
                _logger?.LogWarning(
                    "[音频:采集] 停止采集返回 {Status}：{Detail}",
                    status,
                    AudioCaptureNative.ReadLastError(_handle) ?? AudioCaptureNative.DescribeStatus(status));
            }
            else
            {
                _logger?.LogInformation("[音频:采集] 已停止 WASAPI loopback 采集。");
            }

            return Task.CompletedTask;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_handle != nint.Zero)
            {
                if (_running)
                {
                    AudioCaptureNative.NativeMethods.CaptureStop(_handle);
                    _running = false;
                }

                AudioCaptureNative.NativeMethods.CaptureDestroy(_handle);
                _handle = nint.Zero;
            }

            // 顺序要紧：句柄销毁（内含 stop，同步等线程退出）之后才释放 GCHandle。
            // 反过来则采集线程可能拿着已释放的托管引用回调进来。
            if (_selfHandle.IsAllocated)
            {
                _selfHandle.Free();
            }
        }
    }

    private unsafe void EnsureHandleCreated()
    {
        if (_handle != nint.Zero)
        {
            return;
        }

        _selfHandle = GCHandle.Alloc(this, GCHandleType.Normal);

        var status = AudioCaptureNative.NativeMethods.CaptureCreate(
            &OnNativeFrame,
            GCHandle.ToIntPtr(_selfHandle),
            out var handle);

        if (status != AudioCaptureNative.StatusOk || handle == nint.Zero)
        {
            _selfHandle.Free();
            throw new InvalidOperationException(
                $"创建音频采集器失败：{AudioCaptureNative.DescribeStatus(status)}");
        }

        _handle = handle;
    }

    /// <summary>
    /// native 采集线程的入口。**不得抛出**——异常穿过 FFI 边界是未定义行为，
    /// 在 .NET 上表现为进程直接终止，且崩溃现场看不出真正的错误。
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnNativeFrame(NativeAudioFrame* frame, nint userData)
    {
        try
        {
            if (frame is null || userData == nint.Zero)
            {
                return;
            }

            if (GCHandle.FromIntPtr(userData).Target is WasapiLoopbackFrameSource source)
            {
                source.DispatchFrame(frame);
            }
        }
        catch
        {
            // 吞掉：这里没有任何安全的上抛路径。
        }
    }

    private unsafe void DispatchFrame(NativeAudioFrame* frame)
    {
        var handler = FrameAvailable;
        if (handler is null)
        {
            return;
        }

        var sampleCount = checked((int)frame->FrameCount) * frame->Channels;
        if (sampleCount <= 0 || frame->Samples == nint.Zero)
        {
            return;
        }

        // 按字节整块拷贝而非逐样本转换：native 的 i16 在 Windows x64 上本就是小端布局，
        // 与协议要求的 s16le 逐字节一致，故这里是一次 memcpy 而非格式转换。
        var byteCount = sampleCount * sizeof(short);
        var pcm = new byte[byteCount];
        new ReadOnlySpan<byte>((void*)frame->Samples, byteCount).CopyTo(pcm);

        handler(new AudioFrame(
            pcm,
            (long)frame->QpcPosition,
            (int)frame->SampleRate,
            frame->Channels,
            frame->IsSilent != 0));
    }
}
