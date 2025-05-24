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
using System.Windows;

namespace MediaIsland.Helpers
{
    internal static class ThumbnailHelper
    {
        static ILogger<Plugin>? logger;
        internal static async Task<ImageSource?> GetThumbnail(IRandomAccessStreamReference? thumbnail, bool convertToPng = true, bool isSourceAppSpotify = false)
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
                // 裁剪 Spotify 封面
                if (isSourceAppSpotify)
                {
                    CroppedBitmap croppedBitmap = new(image, new Int32Rect(33, 0, 234, 234));
                    // 创建一个新的BitmapImage对象
                    var croppedImage = new BitmapImage();

                    // 将CroppedBitmap保存到MemoryStream
                    PngBitmapEncoder encoder = new();
                    encoder.Frames.Add(BitmapFrame.Create(croppedBitmap));
                    using (var croppedMs = new MemoryStream())
                    {
                        encoder.Save(croppedMs);
                        croppedMs.Position = 0; // 重置流位置到开头
                        croppedImage.BeginInit();
                        croppedImage.CacheOption = BitmapCacheOption.OnLoad;
                        croppedImage.StreamSource = croppedMs;
                        try
                        {
                            croppedImage.EndInit();
                        }
                        catch (NotSupportedException ex)
                        {
                            logger?.LogError($"加载图片失败: {ex.Message}");
                            return null;
                        }
                    }

                    // 使用裁剪后的图片
                    image = croppedImage;
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