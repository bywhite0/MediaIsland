using System.IO;
using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using MediaIsland.Helpers;
using Microsoft.Extensions.Logging;
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
}
