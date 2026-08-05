using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MediaIsland.Windows.Helpers;

/// <summary>
/// 读取进程可执行文件路径。
/// </summary>
/// <remarks>
/// <see cref="Process.MainModule"/> 需要 PROCESS_VM_READ 权限，遇到提权进程或位数不同的进程
/// （网易云音乐常以管理员身份运行）会抛 <c>Win32Exception</c>。
/// <c>QueryFullProcessImageName</c> 只要 PROCESS_QUERY_LIMITED_INFORMATION 就能拿到路径，
/// 因此作为回退可覆盖这类进程。
/// </remarks>
internal static class WindowsProcessPathHelper
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int InitialPathCapacity = 1024;

    internal static string? TryGetExecutablePath(Process process)
    {
        try
        {
            var fileName = process.MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                return fileName;
            }
        }
        catch
        {
            // 读不到模块信息时改用 Win32 查询。
        }

        return TryQueryImagePath(process);
    }

    private static string? TryQueryImagePath(Process process)
    {
        var handle = IntPtr.Zero;
        try
        {
            handle = OpenProcess(ProcessQueryLimitedInformation, false, process.Id);
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            var buffer = new StringBuilder(InitialPathCapacity);
            var capacity = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref capacity)
                ? buffer.ToString(0, capacity)
                : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                CloseHandle(handle);
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(
        IntPtr process,
        uint flags,
        StringBuilder exeName,
        ref int size);
}
