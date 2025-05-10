// Modified from https://github.com/DubyaDude/WindowsMediaController/blob/master/Sample.UI/MainWindow.xaml.cs#L174-L215
// Copyright (c) 2020 DubyaDude
// Licensed under the MIT License.
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Storage.Streams;
using Microsoft.Extensions.Logging;

namespace MediaIsland.Helpers
{
    internal static class ThumbnailHelper
    {
        static ILogger<Plugin>? logger;
        internal static async Task<ImageSource?> GetThumbnail(IRandomAccessStreamReference? thumbnail, bool convertToPng = true)
        {
            if (thumbnail == null)
                return null;

            try
            {
                using var thumbnailStream = await thumbnail.OpenReadAsync();
                if (thumbnailStream.Size == 0)
                    return null;

                byte[] thumbnailBytes = new byte[thumbnailStream.Size];
                using (var reader = new DataReader(thumbnailStream))
                {
                    await reader.LoadAsync((uint)thumbnailStream.Size);
                    reader.ReadBytes(thumbnailBytes);
                }

                byte[] imageBytes = thumbnailBytes;

                if (convertToPng)
                {
                    try
                    {
                        using var fileMemoryStream = new MemoryStream(thumbnailBytes);
                        using var thumbnailBitmap = (Bitmap)Bitmap.FromStream(fileMemoryStream);

                        if (!thumbnailBitmap.RawFormat.Equals(System.Drawing.Imaging.ImageFormat.Png))
                        {
                            using var pngMemoryStream = new MemoryStream();
                            thumbnailBitmap.Save(pngMemoryStream, System.Drawing.Imaging.ImageFormat.Png);
                            imageBytes = pngMemoryStream.ToArray();
                        }
                    }
                    catch (Exception ex) when (ex is ArgumentException or IOException)
                    {
                        logger?.LogError($"将封面转换为 PNG 失败: {ex.Message}");
                    }
                }

                var image = new BitmapImage();
                using (var ms = new MemoryStream(imageBytes))
                {
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.StreamSource = ms;
                    try
                    {
                        image.EndInit();
                    }
                    catch (NotSupportedException ex)
                    {
                        logger?.LogError($"加载图片失败: {ex.Message}");
                        return null;
                    }
                }

                image.Freeze();
                return image;
            }
            catch (Exception ex) when (ex is IOException or COMException)
            {
                logger?.LogError($"处理封面时发生错误: {ex.Message}");
                return null;
            }
        }
    }
}