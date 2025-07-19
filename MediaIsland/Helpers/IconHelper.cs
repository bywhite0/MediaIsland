using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia.Platform;
using Microsoft.WindowsAPICodePack.Shell;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace MediaIsland.Helpers
{
    public static class IconHelper
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        private const uint SHGFI_ICON = 0x100;
        private const uint SHGFI_LARGEICON = 0x0; // 32x32 pixels
        private const uint SHGFI_SMALLICON = 0x1; // 16x16 pixels

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, out SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        public static Avalonia.Media.Imaging.Bitmap? GetAppIcon(string userModelId)
        {
            try
            {
                // 优先处理UWP应用
                // 较新 Windows 11 版本下 SourceAppUserModelId 可能返回 AppID，尝试当作 UWP 应用处理
                if (userModelId.Contains("!") && userModelId.Contains("_") && userModelId[..^4].Contains("."))
                {
                    try
                    {
                        var shellObj = ShellObject.FromParsingName($"shell:appsFolder\\{userModelId}");
                        if (shellObj?.Thumbnail != null)
                        {
                            return IconToBitmapConverter(shellObj.Thumbnail.LargeIcon);
                        }
                    }
                    catch
                    {
                        // 备用UWP图标获取方式
                        string? packageName = userModelId.Split('!')[0];
                        string? manifestPath = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                            "WindowsApps",
                            packageName,
                            "AppxManifest.xml");

                        if (File.Exists(manifestPath))
                        {
                            var xmlDoc = new System.Xml.XmlDocument();
                            xmlDoc.Load(manifestPath);
                            var logoNode = xmlDoc.SelectSingleNode("//*[local-name()='Logo']");
                            if (logoNode != null)
                            {
                                string logoPath = Path.Combine(
                                    Path.GetDirectoryName(manifestPath),
                                    logoNode.InnerText);
                                return IconToBitmapConverter(new Icon(logoPath));
                            }
                        }
                    }
                }

                // Win32应用处理
                string processName = userModelId.Split('!').Last()
                    .Replace(".exe", "")
                    .Replace("_", "");

                // 通过进程获取
                var processes = Process.GetProcessesByName(processName);
                if (processes.Length > 0)
                {
                    string exePath = processes[0].MainModule.FileName;
                    return IconToBitmapConverter(GetIconFromPath(exePath, SHGFI_LARGEICON));
                }

                // 通过常见安装路径搜索
                string[] searchPaths = {
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                };

                foreach (var path in searchPaths)
                {
                    string possibleExe = Path.Combine(path, $"{processName}.exe");
                    if (File.Exists(possibleExe))
                    {
                        return IconToBitmapConverter(GetIconFromPath(possibleExe, SHGFI_LARGEICON));
                    }

                    // 处理带空格的文件名
                    possibleExe = Path.Combine(path, $"{processName}*.exe");
                    var files = Directory.GetFiles(path, $"{processName}*.exe");
                    if (files.Length > 0)
                    {
                        return IconToBitmapConverter(GetIconFromPath(files[0], SHGFI_LARGEICON));
                    }
                }

                var AppIDPath = AppPathFindHelper.FindExecutablePathByAppUserModelID(userModelId);
                if (AppIDPath != null)
                {
                    return IconToBitmapConverter(GetIconFromPath(AppIDPath, SHGFI_LARGEICON));
                }

                // 尝试获取内部资源
                try
                {
                    var internalIcon = GetInternalIcon(userModelId);
                    if (internalIcon != null)
                    {
                        return internalIcon;
                    }
                }
                catch
                {
                    return IconToBitmapConverter(SystemIcons.Application);
                }

                // 最终回退方案
                return IconToBitmapConverter(SystemIcons.Application);
            }
            catch
            {
                return IconToBitmapConverter(SystemIcons.Application);
            }
        }

        private static Icon? GetIconFromPath(string path, uint flags)
        {
            SHFILEINFO shinfo = new SHFILEINFO();
            IntPtr hImageSmall = SHGetFileInfo(path, 0, out shinfo, (uint)Marshal.SizeOf(shinfo), flags | SHGFI_ICON);

            if (shinfo.hIcon != IntPtr.Zero)
            {
                Icon icon = (Icon)Icon.FromHandle(shinfo.hIcon).Clone();
                DestroyIcon(shinfo.hIcon);
                return icon;
            }

            return null;
        }

        private static Avalonia.Media.Imaging.Bitmap? GetInternalIcon(string AppUserModelId)
        {
            try
            {
                return new Avalonia.Media.Imaging.Bitmap(AssetLoader.Open(new Uri($"avares://MediaIsland/Assets/SourceAppIcons/{AppUserModelId}.png", UriKind.RelativeOrAbsolute)));
            }
            catch
            {
                return null;
            }
        }
        public static Avalonia.Media.Imaging.Bitmap IconToBitmapConverter(Icon icon)
        {
            try
            {
                using var bitmap = icon.ToBitmap();
                using var argbBitmap = new Bitmap(bitmap.Width, bitmap.Height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(argbBitmap))
                {
                    g.DrawImage(bitmap, new Rectangle(0, 0, argbBitmap.Width, argbBitmap.Height));
                }

                using (var stream = new MemoryStream())
                {
                    argbBitmap.Save(stream, ImageFormat.Png);
                    stream.Seek(0, SeekOrigin.Begin);
                    return new Avalonia.Media.Imaging.Bitmap(stream);
                }
            }
            catch (ArgumentNullException)
            {
                throw new ArgumentNullException(nameof(icon));
            }
        }
    }
}
