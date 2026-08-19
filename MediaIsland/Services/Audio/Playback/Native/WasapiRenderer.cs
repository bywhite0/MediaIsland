using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace MediaIsland.Services.Audio.Playback.Native;

/// <summary>
/// 基于 native WASAPI 共享模式的播放器。
///
/// 回调走 <c>[UnmanagedCallersOnly]</c> 静态函数 + <see cref="GCHandle"/>：实例方法无法直接
/// 当函数指针交给 native，而闭包委托会被 GC 移动或回收——那会表现为随机时刻的
/// AccessViolation，且崩溃点与真正的错误相距甚远。这与采集侧的
/// <c>WasapiLoopbackFrameSource</c> 是同一形态。
///
/// PCM 必须在回调内拷出：native 缓冲返回即失效，留着指针晚点读就是读已释放内存。
/// </summary>
internal sealed class WasapiRenderer : IAudioRenderer
{
    /// <summary>每帧字节数：2 声道 × i16。</summary>
    private const int BytesPerFrame = sizeof(short) * 2;

    private const long TicksPerSecond100Ns = 10_000_000;

    private readonly ILogger? _logger;
    private readonly object _gate = new();

    private GCHandle _selfHandle;
    private nint _handle;
    private volatile bool _running;
    private bool _disposed;

    public WasapiRenderer(ILogger? logger = null)
    {
        _logger = logger;
        AudioRenderNative.EnsureResolved(logger);
    }

    public event Action<AudioFrame>? FramePlayed;

    public bool IsAvailable => AudioRenderNative.IsAvailable;

    public string? FailureReason => AudioRenderNative.FailureReason;

    public void Start(int targetBufferMs)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_running)
            {
                return;
            }

            if (!AudioRenderNative.IsAvailable)
            {
                // 抛而不是静默返回：调用方据此回落到不播放的直连路径。
                // 静默失败会让播放开关看起来生效了但没声音。
                throw new InvalidOperationException(
                    $"音频播放不可用：{AudioRenderNative.FailureReason ?? "原因未知"}");
            }

            EnsureHandleCreated();

            var status = AudioRenderNative.NativeMethods.RenderStart(_handle, (uint)targetBufferMs);
            if (status != AudioRenderNative.StatusOk)
            {
                var detail = AudioRenderNative.ReadLastError(_handle)
                             ?? AudioRenderNative.DescribeStatus(status);
                throw new InvalidOperationException($"启动音频播放失败：{detail}");
            }

            _running = true;
            _logger?.LogInformation("[音频:播放] 已启动 WASAPI 播放，目标缓冲 {TargetMs}ms。", targetBufferMs);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_running || _handle == nint.Zero)
            {
                return;
            }

            // native 的 stop 同步等渲染线程退出：返回后回调不可能再触发。
            var status = AudioRenderNative.NativeMethods.RenderStop(_handle);
            _running = false;

            if (status != AudioRenderNative.StatusOk)
            {
                _logger?.LogWarning(
                    "[音频:播放] 停止播放返回 {Status}：{Detail}",
                    status,
                    AudioRenderNative.ReadLastError(_handle) ?? AudioRenderNative.DescribeStatus(status));
            }
            else
            {
                _logger?.LogInformation("[音频:播放] 已停止 WASAPI 播放。");
            }
        }
    }

    public unsafe void Push(byte[] pcm)
    {
        // volatile 快速拒：未播放时不进锁，热路径与启停不争抢。
        var frameCount = FramesToPush(pcm, Volatile.Read(ref _running));
        if (frameCount == 0)
        {
            return;
        }

        // 句柄仍活着这一条必须在锁内复核。只靠上面那次 volatile 读的话，Dispose 可以在
        // 读到 true 与调用 native 之间销毁句柄，RenderPush 就打在已释放的 Box 上。
        // 锁是让这个面在构造上消失的最小手段：未争抢的 Monitor 是数十纳秒，
        // 对 20ms 一帧差六个数量级；真正的争抢只来自罕见的启停，
        // 而那时阻塞一次 Push 恰是想要的行为。
        //
        // 代价是 FramePlayed 的处理器不得回调进 Push 或 Stop——停播时渲染线程在 join，
        // 它若在等 _gate 就互等。接口注释已把这条写成约定。
        lock (_gate)
        {
            if (!_running || _handle == nint.Zero)
            {
                return;
            }

            // 协议 PCM 已是 s16le 交错，与 native 期望的 i16 逐字节一致，
            // 故这里是一次 memcpy 而非格式转换。
            fixed (byte* raw = pcm)
            {
                AudioRenderNative.NativeMethods.RenderPush(_handle, (short*)raw, frameCount);
            }
        }
    }

    /// <summary>
    /// 本次调用该送几帧。零表示不送。
    ///
    /// 抽成纯函数不是为了复用，是为了让这两条判定可测。<paramref name="running"/> 这道门若失守，
    /// native 只会对零句柄返回 <c>INVALID_ARG</c>，而托管侧不看返回值——缺陷会完全静默。
    /// 帧数必须向下取整到整帧：传输侧只保证 PCM 字节数模 2（<c>MediaLinkAudioReceiver</c>
    /// 只拦奇数长度），不保证模 4，而 native 的环形缓冲不跨调用结转半帧。
    ///
    /// 不足一帧得零由整数除法本身给出，不另设下界判断——那会是一条无任何变异能区分的冗余分支。
    /// </summary>
    internal static nuint FramesToPush(byte[]? pcm, bool running) =>
        !running || pcm is null ? 0 : (nuint)(pcm.Length / BytesPerFrame);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _running = false;

            if (_handle != nint.Zero)
            {
                AudioRenderNative.NativeMethods.RenderDestroy(_handle);
                _handle = nint.Zero;
            }

            // 顺序要紧：句柄销毁（内含 stop，同步等线程退出）之后才释放 GCHandle。
            // 反过来则渲染线程可能拿着已释放的托管引用回调进来。
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

        var status = AudioRenderNative.NativeMethods.RenderCreate(
            &OnNativePlayedFrame,
            GCHandle.ToIntPtr(_selfHandle),
            out var handle);

        if (status != AudioRenderNative.StatusOk || handle == nint.Zero)
        {
            _selfHandle.Free();
            throw new InvalidOperationException(
                $"创建音频播放器失败：{AudioRenderNative.DescribeStatus(status)}");
        }

        _handle = handle;
    }

    /// <summary>
    /// native 渲染线程的入口。不得抛出——异常穿过 FFI 边界是未定义行为，
    /// 在 .NET 上表现为进程直接终止，且崩溃现场看不出真正的错误。
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnNativePlayedFrame(NativePlayedFrame* frame, nint userData)
    {
        try
        {
            if (frame is null || userData == nint.Zero)
            {
                return;
            }

            if (GCHandle.FromIntPtr(userData).Target is WasapiRenderer renderer)
            {
                renderer.DispatchPlayedFrame(frame);
            }
        }
        catch
        {
            // 吞掉：这里没有任何安全的上抛路径。
        }
    }

    private unsafe void DispatchPlayedFrame(NativePlayedFrame* frame)
    {
        var handler = FramePlayed;
        if (handler is null)
        {
            return;
        }

        var sampleCount = checked((int)frame->FrameCount) * frame->Channels;
        if (sampleCount <= 0 || frame->Samples == nint.Zero)
        {
            return;
        }

        var byteCount = sampleCount * sizeof(short);
        var pcm = new byte[byteCount];
        new ReadOnlySpan<byte>((void*)frame->Samples, byteCount).CopyTo(pcm);

        // native 的 PlayedFrame 不带 QPC 读数，故填本地观测时刻，算法与
        // MediaLinkAudioReceiver.DefaultQpc100Ns 一致——Stopwatch.Frequency
        // 不保证等于 10^7，须显式归一到 100ns 而非假定两者刻度相同。
        //
        // IsSilent 恒假：已播出的块即使内容全零也代表「这一刻扬声器在按时间轴前进」，
        // 静音标志是上游给的语义，播放侧无从判断也不该猜。
        handler(new AudioFrame(
            pcm,
            (long)(Stopwatch.GetTimestamp() * (double)TicksPerSecond100Ns / Stopwatch.Frequency),
            (int)frame->SampleRate,
            frame->Channels,
            IsSilent: false));
    }
}
