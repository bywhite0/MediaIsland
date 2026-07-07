using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Microsoft.Extensions.Logging;

namespace MediaIsland.Windows.Helpers;

internal static class WindowsShellIconHelper
{
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiLargeIcon = 0x000000000;
    private const uint BiRgb = 0;
    private const uint DibRgbColors = 0;
    private const int DiNormal = 0x0003;

    internal static Bitmap? GetFileIcon(string filePath, ILogger logger)
    {
        var hResult = SHGetFileInfo(
            filePath,
            0,
            out var fileInfo,
            (uint)Marshal.SizeOf<ShellFileInfo>(),
            ShgfiIcon | ShgfiLargeIcon);
        if (hResult == IntPtr.Zero || fileInfo.Icon == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return ConvertIconToBitmap(fileInfo.Icon);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "无法读取 Windows Shell 图标：{FilePath}", filePath);
            return null;
        }
        finally
        {
            DestroyIcon(fileInfo.Icon);
        }
    }

    private static Bitmap? ConvertIconToBitmap(IntPtr icon)
    {
        if (!GetIconInfo(icon, out var iconInfo))
        {
            return null;
        }

        try
        {
            var bitmapHandle = iconInfo.ColorBitmap != IntPtr.Zero
                ? iconInfo.ColorBitmap
                : iconInfo.MaskBitmap;
            if (bitmapHandle == IntPtr.Zero ||
                GetObject(bitmapHandle, Marshal.SizeOf<GdiBitmap>(), out var bitmap) == 0 ||
                bitmap.Width <= 0 ||
                bitmap.Height <= 0)
            {
                return null;
            }

            var width = bitmap.Width;
            var height = iconInfo.ColorBitmap == IntPtr.Zero ? bitmap.Height / 2 : bitmap.Height;
            var stride = width * 4;
            var pixels = new byte[stride * height];
            var bitmapInfo = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = width,
                    Height = -height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = BiRgb,
                    SizeImage = (uint)pixels.Length
                }
            };

            var dibPixels = IntPtr.Zero;
            var dib = CreateDIBSection(
                IntPtr.Zero,
                ref bitmapInfo,
                DibRgbColors,
                out dibPixels,
                IntPtr.Zero,
                0);
            if (dib == IntPtr.Zero || dibPixels == IntPtr.Zero)
            {
                return null;
            }

            var memoryDc = CreateCompatibleDC(IntPtr.Zero);
            try
            {
                if (memoryDc == IntPtr.Zero)
                {
                    return null;
                }

                var oldObject = SelectObject(memoryDc, dib);
                try
                {
                    if (!DrawIconEx(memoryDc, 0, 0, icon, width, height, 0, IntPtr.Zero, DiNormal))
                    {
                        return null;
                    }

                    Marshal.Copy(dibPixels, pixels, 0, pixels.Length);
                }
                finally
                {
                    if (oldObject != IntPtr.Zero)
                    {
                        SelectObject(memoryDc, oldObject);
                    }
                }
            }
            finally
            {
                if (memoryDc != IntPtr.Zero)
                {
                    DeleteDC(memoryDc);
                }

                if (dib != IntPtr.Zero)
                {
                    DeleteObject(dib);
                }
            }

            return CreateAvaloniaBitmap(pixels, width, height, stride);
        }
        finally
        {
            if (iconInfo.ColorBitmap != IntPtr.Zero)
            {
                DeleteObject(iconInfo.ColorBitmap);
            }

            if (iconInfo.MaskBitmap != IntPtr.Zero)
            {
                DeleteObject(iconInfo.MaskBitmap);
            }
        }
    }

    private static Bitmap CreateAvaloniaBitmap(byte[] pixels, int width, int height, int stride)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);
        using var lockedBitmap = bitmap.Lock();
        Marshal.Copy(pixels, 0, lockedBitmap.Address, pixels.Length);
        return bitmap;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string path,
        uint fileAttributes,
        out ShellFileInfo fileInfo,
        uint fileInfoSize,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetIconInfo(IntPtr icon, out IconInfo iconInfo);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int GetObject(IntPtr handle, int count, out GdiBitmap bitmap);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(
        IntPtr deviceContext,
        ref BitmapInfo bitmapInfo,
        uint usage,
        out IntPtr bits,
        IntPtr section,
        uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr handle);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteDC(IntPtr deviceContext);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DrawIconEx(
        IntPtr deviceContext,
        int left,
        int top,
        IntPtr icon,
        int width,
        int height,
        uint stepIfAniCursor,
        IntPtr flickerFreeDraw,
        int flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool IsIcon;

        public int HotspotX;
        public int HotspotY;
        public IntPtr MaskBitmap;
        public IntPtr ColorBitmap;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GdiBitmap
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public IntPtr Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }
}
