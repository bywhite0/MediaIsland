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

                using var ms = new MemoryStream(imageBytes);
                // 统一处理图片：裁剪（Spotify  可能的水印）和缩放（512x512）
                return ProcessThumbnail(ms, isSourceAppSpotify);
            }
            catch (Exception ex) when (ex is IOException or COMException)
            {
                logger?.LogError($"处理封面时发生错误: {ex.Message}");
                return null;
            }
        }


        /// <summary>
        /// 处理缩略图，包括可选的裁剪和缩放。
        /// </summary>
        /// <param name="inputStream">原始图像流</param>
        /// <param name="isSpotify">是否为 Spotify 来源（需要特殊裁剪）</param>
        internal static Bitmap? ProcessThumbnail(Stream inputStream, bool isSpotify)
        {
            try
            {
                inputStream.Seek(0, SeekOrigin.Begin);
                using var skStream = new SKManagedStream(inputStream);
                using var codec = SKCodec.Create(skStream);
                if (codec == null) return null;
                using var originalBitmap = SKBitmap.Decode(codec);
                if (originalBitmap == null) return null;

                SKBitmap currentBitmap = originalBitmap;
                bool isNewBitmap = false;

                // 1. 裁剪 Spotify 封面
                if (isSpotify)
                {
                    var cropRect = new SKRectI(33, 0, 234, 234);
                    var cropped = new SKBitmap(cropRect.Width, cropRect.Height);
                    using (var canvas = new SKCanvas(cropped))
                    {
                        var srcRect = cropRect;
                        var dstRect = new SKRect(0, 0, cropRect.Width, cropRect.Height);
                        canvas.DrawBitmap(currentBitmap, srcRect, dstRect);
                    }
                    currentBitmap = cropped;
                    isNewBitmap = true;
                }

                // 2. 缩小至 512x512
                const int targetSize = 512;
                if (currentBitmap.Width > targetSize || currentBitmap.Height > targetSize)
                {
                    // 计算缩放比例以保持宽高比
                    float ratio = Math.Min((float)targetSize / currentBitmap.Width, (float)targetSize / currentBitmap.Height);
                    int newWidth = (int)(currentBitmap.Width * ratio);
                    int newHeight = (int)(currentBitmap.Height * ratio);

                    var resized = currentBitmap.Resize(new SKImageInfo(newWidth, newHeight), SKFilterQuality.High);
                    if (resized != null)
                    {
                        if (isNewBitmap) currentBitmap.Dispose();
                        currentBitmap = resized;
                        isNewBitmap = true;
                    }
                }

                using var outStream = new MemoryStream();
                using (var skImage = SKImage.FromBitmap(currentBitmap))
                using (var data = skImage.Encode(SKEncodedImageFormat.Png, 100))
                {
                    data.SaveTo(outStream);
                }

                if (isNewBitmap) currentBitmap.Dispose();

                outStream.Seek(0, SeekOrigin.Begin);
                return new Bitmap(outStream);
            }
            catch (Exception ex)
            {
                logger?.LogError($"处理封面图片时发生错误: {ex.Message}");
                return null;
            }
        }
    }
}