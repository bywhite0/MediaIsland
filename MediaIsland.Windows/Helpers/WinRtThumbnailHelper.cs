using System.IO;
using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using MediaIsland.Helpers;
using Microsoft.Extensions.Logging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace MediaIsland.Windows.Helpers;

internal static class WinRtThumbnailHelper
{
    internal static async Task<Bitmap?> GetThumbnail(
        IRandomAccessStreamReference? thumbnail,
        bool isSourceAppSpotify,
        ILogger logger)
    {
        if (thumbnail == null)
        {
            return null;
        }

        try
        {
            using var thumbnailStream = await thumbnail.OpenReadAsync();
            if (thumbnailStream.Size == 0)
            {
                return null;
            }

            var thumbnailBytes = new byte[thumbnailStream.Size];
            using (var reader = new DataReader(thumbnailStream))
            {
                await reader.LoadAsync((uint)thumbnailStream.Size);
                reader.ReadBytes(thumbnailBytes);
            }

            using var stream = new MemoryStream(thumbnailBytes);
            return ThumbnailHelper.ProcessThumbnail(stream, isSourceAppSpotify);
        }
        catch (Exception ex) when (ex is IOException or COMException)
        {
            logger.LogError(ex, "处理封面时发生错误。");
            return null;
        }
    }

    internal static async Task<Bitmap?> GetBitmapAsync(
        IRandomAccessStream? thumbnailStream,
        ILogger logger)
    {
        if (thumbnailStream == null)
        {
            return null;
        }

        try
        {
            if (thumbnailStream.Size == 0)
            {
                return null;
            }

            using var pngStream = await EncodePngAsync(thumbnailStream);
            return new Bitmap(pngStream);
        }
        catch (Exception ex) when (ex is IOException or COMException)
        {
            logger.LogDebug(ex, "读取 Windows 图标缩略图时发生错误。");
            return null;
        }
    }

    private static async Task<MemoryStream> EncodePngAsync(IRandomAccessStream sourceStream)
    {
        sourceStream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(sourceStream);
        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Rgba8,
            BitmapAlphaMode.Premultiplied,
            new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);

        using var encodedStream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, encodedStream);
        encoder.SetPixelData(
            BitmapPixelFormat.Rgba8,
            BitmapAlphaMode.Premultiplied,
            decoder.PixelWidth,
            decoder.PixelHeight,
            decoder.DpiX,
            decoder.DpiY,
            pixelData.DetachPixelData());
        await encoder.FlushAsync();

        encodedStream.Seek(0);
        var bytes = new byte[encodedStream.Size];
        using (var reader = new DataReader(encodedStream))
        {
            await reader.LoadAsync((uint)encodedStream.Size);
            reader.ReadBytes(bytes);
        }

        return new MemoryStream(bytes);
    }
}
