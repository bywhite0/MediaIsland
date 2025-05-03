// From https://github.com/DubyaDude/WindowsMediaController/blob/master/Sample.UI/MainWindow.xaml.cs#L174-L215
// Copyright (c) 2020 DubyaDude
// Licensed under the MIT License.

using System.Drawing;
using System.Windows.Media.Imaging;
using Windows.Storage.Streams;

namespace MediaIsland.Helpers
{
    internal static class ThumbnailHelper
    {
        internal static BitmapImage? GetThumbnail(IRandomAccessStreamReference Thumbnail, bool convertToPng = true)
        {
            if (Thumbnail == null)
                return null;

            var thumbnailStream = Thumbnail.OpenReadAsync().GetAwaiter().GetResult();
            byte[] thumbnailBytes = new byte[thumbnailStream.Size];
            using (DataReader reader = new DataReader(thumbnailStream))
            {
                reader.LoadAsync((uint)thumbnailStream.Size).GetAwaiter().GetResult();
                reader.ReadBytes(thumbnailBytes);
            }

            byte[] imageBytes = thumbnailBytes;

            if (convertToPng)
            {
                using var fileMemoryStream = new System.IO.MemoryStream(thumbnailBytes);
                Bitmap thumbnailBitmap = (Bitmap)Bitmap.FromStream(fileMemoryStream);

                if (!thumbnailBitmap.RawFormat.Equals(System.Drawing.Imaging.ImageFormat.Png))
                {
                    using var pngMemoryStream = new System.IO.MemoryStream();
                    thumbnailBitmap.Save(pngMemoryStream, System.Drawing.Imaging.ImageFormat.Png);
                    imageBytes = pngMemoryStream.ToArray();
                }
            }

            var image = new BitmapImage();
            using (var ms = new System.IO.MemoryStream(imageBytes))
            {
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = ms;
                image.EndInit();
            }

            return image;
        }
    }
}
