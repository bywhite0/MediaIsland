using System.IO;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace MediaIsland.Helpers
{
    public static class ThumbnailHelper
    {
        /// <summary>
        /// 处理缩略图，包括可选的裁剪和缩放。
        /// </summary>
        /// <param name="inputStream">原始图像流</param>
        /// <param name="isSpotify">是否为 Spotify 来源（需要特殊裁剪）</param>
        public static Bitmap? ProcessThumbnail(Stream inputStream, bool isSpotify)
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
            catch
            {
                return null;
            }
        }
    }
}
