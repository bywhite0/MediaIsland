// Modified from https://github.com/DubyaDude/WindowsMediaController/blob/master/Sample.UI/MainWindow.xaml.cs#L174-L215
// Copyright (c) 2020 DubyaDude
// Licensed under the MIT License.
// using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using Windows.Storage.Streams;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace MediaIsland.Helpers
{
    internal static class ThumbnailHelper
    {
        static ILogger<Plugin>? logger;
        internal static async Task<Bitmap?> GetThumbnail(IRandomAccessStreamReference? thumbnail, bool convertToPng = true, bool isSourceAppSpotify = false)
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

                var ms = new MemoryStream(imageBytes);
                var image = new Bitmap(ms);
                // 裁剪 Spotify 封面
                if (isSourceAppSpotify)
                {
                    var cropRect = new SKRectI(33, 0, 234, 234);
                    return CropBitmap(ms, cropRect);
                }

                return image;
            }
            catch (Exception ex) when (ex is IOException or COMException)
            {
                logger?.LogError($"处理封面时发生错误: {ex.Message}");
                return null;
            }
        }


        /// <summary>
        /// 将图像从流中加载，并裁剪指定区域。
        /// </summary>
        /// <param name="inputStream">原始图像流</param>
        /// <param name="cropRect">裁剪区域（单位像素）</param>
        internal static Bitmap CropBitmap(Stream inputStream, SKRectI cropRect)
        {
            inputStream.Seek(0, SeekOrigin.Begin);
            using var skStream = new SKManagedStream(inputStream);
            using var codec = SKCodec.Create(skStream);
            using var originalBitmap = SKBitmap.Decode(codec);

            using var croppedBitmap = new SKBitmap(cropRect.Width, cropRect.Height);
            using (var canvas = new SKCanvas(croppedBitmap))
            {
                var srcRect = cropRect;
                var dstRect = new SKRect(0, 0, cropRect.Width, cropRect.Height);
                canvas.DrawBitmap(originalBitmap, srcRect, dstRect);
            }

            using var croppedStream = new MemoryStream();
            using (var skImage = SKImage.FromBitmap(croppedBitmap))
            using (var data = skImage.Encode(SKEncodedImageFormat.Png, 100))
            {
                data.SaveTo(croppedStream);
            }

            croppedStream.Seek(0, SeekOrigin.Begin);
            return new Bitmap(croppedStream);
        }
    }
}