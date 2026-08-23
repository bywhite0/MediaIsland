using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace MediaIsland.Services.Audio.Playback.Native;

/// <summary>已播出块的原始载荷。字段布局是 Rust <c>#[repr(C)] PlayedFrame</c> 的镜像。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativePlayedFrame
{
    /// <summary>交错 i16。仅在回调期间有效，返回后立即失效。</summary>
    public nint Samples;

    public nuint FrameCount;
    public uint SampleRate;
    public ushort Channels;
}

/// <summary>
/// 播放统计的原始载荷。字段布局是 Rust <c>#[repr(C)] RenderStats</c> 的镜像。
///
/// 字段一律取 8 字节宽是刻意的：混用 32 位与 64 位会让布局出现 padding，
/// 而跨 FFI 的布局错位是静默的——读到的是别的字段的值。
/// <c>AudioRenderStatsTests.NativeLayout_PinsEveryFieldOffset</c> 与 Rust 侧的
/// <c>render_stats_layout_has_no_padding</c> 一起钉住这一点，且两条各钉自己那一侧的
/// 逐字段偏移——只钉一侧时，两侧的字段顺序可以在两条判据都绿的情况下不一致。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeRenderStats
{
    public ulong RingFrames;
    public ulong UnderrunCount;
    public ulong HardResetCount;
    public ulong DeviceFramesRendered;
    public ulong DeviceSampleRate;
    public ulong ResampleRatioPpm;
    public ulong DevicePositionFrames;
    public ulong DevicePositionQpc;
    public ulong DeviceLatencyUs;

    /// <summary>
    /// 外环误差，微秒。唯一的有符号字段——不用「加偏置存成无符号」那种编码：
    /// 偏置是一个必须两侧同时记得的约定，而 long 与 i64 在两侧都是原生类型。
    /// </summary>
    public long PlayTimeErrorUs;
    public ulong TargetMsCurrent;

    /// <summary>
    /// 0 或 1。本来一个 uint 就够，取 ulong 是为了不引入尾部填充——
    /// 填充的大小两端各自按对齐规则推，那是又一处不必存在的约定。
    /// </summary>
    public ulong ClockOffsetAvailable;
}

/// <summary>
/// <c>MediaIsland.Audio</c> 播放侧的 P/Invoke 绑定。
///
/// 降级模式与采集侧的 <c>AudioCaptureNative</c> 同形：首次访问时探 ABI 版本，DLL 缺失或版本
/// 不匹配则 <see cref="IsAvailable"/> 转假并记下 <see cref="FailureReason"/>，不抛给调用方。
/// 播放因此成为可选能力——接收、转发、可视化都是纯托管的，在没有 native 库的平台上照常工作。
///
/// 探测状态与采集绑定各自独立，但两者读的是同一个 <c>mediaisland_audio_abi_version</c>，
/// 故 <see cref="ExpectedAbiVersion"/> 必须与采集侧的同名常量同步升降。
/// </summary>
internal static partial class AudioRenderNative
{
    public const uint ExpectedAbiVersion = 4;

    public const int StatusOk = 0;
    public const int StatusInvalidArg = 1;
    public const int StatusUnsupportedPlatform = 2;
    public const int StatusDeviceError = 3;
    public const int StatusAlreadyRunning = 4;
    public const int StatusPanic = 5;

    private static readonly object Gate = new();
    private static bool _resolved;
    private static bool _available;
    private static string? _failureReason;

    public static bool IsAvailable
    {
        get
        {
            EnsureResolved();
            return _available;
        }
    }

    public static string? FailureReason
    {
        get
        {
            EnsureResolved();
            return _failureReason;
        }
    }

    public static void EnsureResolved(ILogger? logger = null)
    {
        if (Volatile.Read(ref _resolved))
        {
            return;
        }

        lock (Gate)
        {
            if (_resolved)
            {
                return;
            }

            try
            {
                var version = NativeMethods.AbiVersion();
                if (version != ExpectedAbiVersion)
                {
                    _available = false;
                    _failureReason = $"ABI 版本不匹配：期望 {ExpectedAbiVersion}，实际为 {version}";
                    logger?.LogWarning("[音频:原生] {Reason}", _failureReason);
                }
                else
                {
                    _available = true;
                    _failureReason = null;
                }
            }
            catch (DllNotFoundException ex)
            {
                _available = false;
                _failureReason = "找不到 MediaIsland.Audio 原生库";
                logger?.LogWarning(ex, "[音频:原生] {Reason}", _failureReason);
            }
            catch (Exception ex)
            {
                _available = false;
                _failureReason = ex.Message;
                logger?.LogWarning(ex, "[音频:原生] 无法解析原生库。");
            }
            finally
            {
                Volatile.Write(ref _resolved, true);
            }
        }
    }

    /// <summary>取 native 侧最近一次错误描述。取不到时返回 null 而非空串。</summary>
    public static string? ReadLastError(nint handle)
    {
        if (handle == nint.Zero)
        {
            return null;
        }

        var buffer = default(FfiBuffer);
        try
        {
            buffer = NativeMethods.RenderLastError(handle);
            if (buffer.Ptr == nint.Zero || buffer.Len == 0)
            {
                return null;
            }

            var length = checked((int)buffer.Len);
            var bytes = new byte[length];
            Marshal.Copy(buffer.Ptr, bytes, 0, length);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (buffer.Ptr != nint.Zero && buffer.Len > 0)
            {
                try
                {
                    NativeMethods.Free(buffer.Ptr, buffer.Len);
                }
                catch
                {
                    // ignored
                }
            }
        }
    }

    public static string DescribeStatus(int status) => status switch
    {
        StatusOk => "成功",
        StatusInvalidArg => "参数无效",
        StatusUnsupportedPlatform => "当前平台不支持音频播放",
        StatusDeviceError => "音频设备错误",
        StatusAlreadyRunning => "播放已在运行",
        StatusPanic => "原生库内部错误",
        _ => $"未知状态 {status}"
    };

    /// <summary>仅供测试：重置探测结果，使下一次访问重新解析。</summary>
    internal static void ResetForTesting(bool? available = null, string? failureReason = null)
    {
        lock (Gate)
        {
            if (available is null)
            {
                Volatile.Write(ref _resolved, false);
                _available = false;
                _failureReason = null;
            }
            else
            {
                _available = available.Value;
                _failureReason = failureReason;
                Volatile.Write(ref _resolved, true);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FfiBuffer
    {
        public nint Ptr;
        public nuint Len;
        public int Status;
    }

    internal static partial class NativeMethods
    {
        private const string LibraryName = "MediaIsland.Audio";

        [LibraryImport(LibraryName, EntryPoint = "mediaisland_audio_abi_version")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial uint AbiVersion();

        [LibraryImport(LibraryName, EntryPoint = "mediaisland_audio_render_create")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static unsafe partial int RenderCreate(
            delegate* unmanaged[Cdecl]<NativePlayedFrame*, nint, void> callback,
            nint userData,
            out nint handle);

        [LibraryImport(LibraryName, EntryPoint = "mediaisland_audio_render_start")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial int RenderStart(nint handle, uint targetBufferMs);

        [LibraryImport(LibraryName, EntryPoint = "mediaisland_audio_render_push")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static unsafe partial int RenderPush(
            nint handle,
            short* samples,
            nuint frameCount,
            long senderTicks);

        [LibraryImport(LibraryName, EntryPoint = "mediaisland_audio_render_set_alignment")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial int RenderSetAlignment(
            nint handle,
            [MarshalAs(UnmanagedType.U1)] bool enabled,
            long dTicks,
            long offsetTicks,
            long manualOffsetTicks);

        [LibraryImport(LibraryName, EntryPoint = "mediaisland_audio_render_stop")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial int RenderStop(nint handle);

        [LibraryImport(LibraryName, EntryPoint = "mediaisland_audio_render_destroy")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial void RenderDestroy(nint handle);

        [LibraryImport(LibraryName, EntryPoint = "mediaisland_audio_render_last_error")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial FfiBuffer RenderLastError(nint handle);

        [LibraryImport(LibraryName, EntryPoint = "mediaisland_audio_render_stats")]
        public static unsafe partial int RenderStats(nint handle, NativeRenderStats* stats);

        [LibraryImport(LibraryName, EntryPoint = "mediaisland_audio_free")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial void Free(nint ptr, nuint len);
    }
}
