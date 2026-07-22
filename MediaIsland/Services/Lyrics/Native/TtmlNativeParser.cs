using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MediaIsland.Services.Lyrics.Native;

public sealed partial class TtmlNativeParser
{
    public const uint ExpectedAbiVersion = 1;

    private readonly ILogger<TtmlNativeParser>? _logger;
    private readonly object _gate = new();
    private bool _resolved;
    private bool _available;
    private string? _failureReason;

    public TtmlNativeParser(ILogger<TtmlNativeParser>? logger = null)
    {
        _logger = logger;
    }

    public bool IsAvailable
    {
        get
        {
            EnsureResolved();
            return _available;
        }
    }

    public string? FailureReason
    {
        get
        {
            EnsureResolved();
            return _failureReason;
        }
    }

    public string? ParseToAmllJson(string ttml)
    {
        if (string.IsNullOrWhiteSpace(ttml))
        {
            return null;
        }

        EnsureResolved();
        if (!_available)
        {
            return null;
        }

        var input = Encoding.UTF8.GetBytes(ttml);
        var buffer = default(FfiBuffer);
        try
        {
            buffer = NativeMethods.ParseToAmllJson(input, (nuint)input.Length, null, 0);
            if (buffer.Status != 0 || buffer.Ptr == nint.Zero || buffer.Len == 0)
            {
                var error = buffer.Ptr != nint.Zero && buffer.Len > 0
                    ? Encoding.UTF8.GetString(ReadNativeBytes(buffer.Ptr, buffer.Len))
                    : $"status={buffer.Status}";
                _logger?.LogWarning("[歌词:原生TTML] 解析失败：{Error}", Truncate(error, 240));
                return null;
            }

            var bytes = ReadNativeBytes(buffer.Ptr, buffer.Len);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (DllNotFoundException ex)
        {
            _available = false;
            _failureReason = ex.Message;
            _logger?.LogWarning(ex, "[歌词:原生TTML] 缺少原生库。");
            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[歌词:原生TTML] 解析时发生未知错误。");
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

    private void EnsureResolved()
    {
        if (_resolved)
        {
            return;
        }

        lock (_gate)
        {
            if (_resolved)
            {
                return;
            }

            _resolved = true;
            try
            {
                var version = NativeMethods.AbiVersion();
                if (version != ExpectedAbiVersion)
                {
                    _available = false;
                    _failureReason = $"ABI 版本不匹配：期望 {ExpectedAbiVersion}，实际为 {version}";
                    _logger?.LogWarning("[歌词:原生TTML] {Reason}", _failureReason);
                    return;
                }

                _available = true;
                _failureReason = null;
            }
            catch (DllNotFoundException ex)
            {
                _available = false;
                _failureReason = "找不到 MediaIsland.Ttml 原生库";
                _logger?.LogWarning(ex, "[歌词:原生TTML] {Reason}", _failureReason);
            }
            catch (Exception ex)
            {
                _available = false;
                _failureReason = ex.Message;
                _logger?.LogWarning(ex, "[歌词:原生TTML] 无法解析原生库。");
            }
        }
    }

    private static byte[] ReadNativeBytes(nint ptr, nuint len)
    {
        var length = checked((int)len);
        var bytes = new byte[length];
        Marshal.Copy(ptr, bytes, 0, length);
        return bytes;
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max];

    [StructLayout(LayoutKind.Sequential)]
    private struct FfiBuffer
    {
        public nint Ptr;
        public nuint Len;
        public int Status;
    }

    private static partial class NativeMethods
    {
        private const string LibraryName = "MediaIsland.Ttml";

        [LibraryImport(LibraryName, EntryPoint = "mediaisland_ttml_abi_version")]
        [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        public static partial uint AbiVersion();

        [LibraryImport(LibraryName, EntryPoint = "mediaisland_ttml_parse_to_amll_json")]
        [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        public static partial FfiBuffer ParseToAmllJson(
            byte[] input,
            nuint inputLen,
            byte[]? options,
            nuint optionsLen);

        [LibraryImport(LibraryName, EntryPoint = "mediaisland_ttml_free")]
        [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        public static partial void Free(nint ptr, nuint len);
    }
}
