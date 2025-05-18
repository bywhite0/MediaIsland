using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MediaIsland.Helpers
{
    class AppPathFindHelper
    {
        private const int ERROR_INSUFFICIENT_BUFFER = 122;

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

        [DllImport("shell32.dll")]
        private static extern int SHGetPropertyStoreForWindow(
            IntPtr hwnd,
            ref Guid iid,
            out IPropertyStore propertyStore);

        [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [ComImport]
        private interface IPropertyStore
        {
            int GetCount(out uint cProps);
            int GetAt(uint iProp, out PROPERTYKEY pkey);
            int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
            int SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
            int Commit();
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct PROPERTYKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROPVARIANT
        {
            public ushort vt;
            public ushort wReserved1;
            public ushort wReserved2;
            public ushort wReserved3;
            public IntPtr p;
            public int dummy;
        }

        private static readonly PROPERTYKEY AppUserModelIDKey = new PROPERTYKEY
        {
            fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
            pid = 5
        };

        public static string? FindExecutablePathByAppUserModelID(string targetAppID)
        {
            string? result = null;

            EnumWindows((hWnd, lParam) =>
            {
                if (!IsWindowVisible(hWnd)) return true;

                var iid = typeof(IPropertyStore).GUID;
                if (SHGetPropertyStoreForWindow(hWnd, ref iid, out var propStore) == 0)
                {
                    PROPVARIANT pv;
                    if (propStore.GetValue(AppUserModelIDKey, out pv) == 0)
                    {
                        string? appId = Marshal.PtrToStringUni(pv.p);
                        if (appId != null && appId.Equals(targetAppID, StringComparison.OrdinalIgnoreCase))
                        {
                            GetWindowThreadProcessId(hWnd, out int pid);
                            try
                            {
                                using var proc = Process.GetProcessById(pid);
                                result = proc.MainModule?.FileName;
                                Console.WriteLine(result);
                            }
                            catch { }
                            return false; // stop enumeration
                        }
                    }
                }

                return true;
            }, IntPtr.Zero);

            return result;
        }

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);
    }
}
