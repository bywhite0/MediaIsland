using System.Runtime.InteropServices;

namespace MediaIsland.Services.Audio.Playback;

/// <summary>
/// 系统默认渲染端点的标识。per-device 手动偏移要一个「当前播放设备」的键，
/// 而 native 渲染起播时恒绑默认渲染端点（应用没有设备选择），端点 ID 又不经
/// FFI 回传——故托管侧用同一条规则（eRender + eConsole 的默认端点）取同一个
/// 端点的 ID。两侧指向同一 API 的同一对参数，不构成第二个设备概念。
///
/// 已知窗口：共享模式的 WASAPI 流不随默认设备切换迁移。播放中换默认设备时，
/// 声音仍从旧端点出，而这里返回新端点 ID，直到下一次停播重启才重新一致。
/// 这个窗口里按新端点取偏移不劣于此前恒传 0 的行为。
/// </summary>
internal static class DefaultRenderEndpoint
{
    private const int DataFlowRender = 0;
    private const int RoleConsole = 0;

    /// <summary>
    /// 取默认渲染端点的 ID。没有音频设备或 COM 调用失败时返回 null——
    /// 调用方把 null 当「未知设备」，偏移取 0。
    /// </summary>
    public static string? TryGetId()
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            try
            {
                if (enumerator.GetDefaultAudioEndpoint(DataFlowRender, RoleConsole, out var device) != 0
                    || device is null)
                {
                    return null;
                }

                try
                {
                    if (device.GetId(out var idPtr) != 0 || idPtr == 0)
                    {
                        return null;
                    }

                    try
                    {
                        return Marshal.PtrToStringUni(idPtr);
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(idPtr);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(device);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }
        catch
        {
            // 拿不到设备标识不是故障路径的起点：偏移退到 0，对齐照常判。
            return null;
        }
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject
    {
    }

    // 只声明用到的槽位之前的方法，vtable 序不可动；后面的槽位从不触碰，可以省略。
    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, int stateMask, out nint devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(
            int dataFlow, int role, [MarshalAs(UnmanagedType.Interface)] out IMMDevice? endpoint);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, int clsCtx, nint activationParams, out nint instance);

        [PreserveSig]
        int OpenPropertyStore(int storageAccessMode, out nint properties);

        [PreserveSig]
        int GetId(out nint id);
    }
}
