using Avalonia.Media.Imaging;
using MediaIsland.Services.Media;

namespace MediaIsland.Services.MediaLink;

/// <summary>
/// 封面编码。HTTP <c>/v1/thumbnail</c> 与 WebSocket <c>thumbnail.get</c> 共用同一实现，
/// 避免两条路径对"有没有封面"给出不一致的答案。
/// </summary>
public static class MediaLinkThumbnail
{
    /// <summary>wire 上的封面 MIME 类型。Avalonia 的 <c>Bitmap.Save</c> 输出 PNG。</summary>
    public const string MimeType = "image/png";

    /// <summary>
    /// 单张封面字节上限（1 MiB）。base64 后约 1.37 MiB，仍低于入站单帧 2 MiB 上限。
    /// 出站队列每会话 64 槽且并发会话上限 32，无上限的大帧会把内存放大到不可接受的量级。
    /// </summary>
    public const int MaxBytes = 1024 * 1024;

    /// <summary>
    /// 把封面编码为 PNG 字节。无封面返回 null；超过 <see cref="MaxBytes"/> 也返回 null，
    /// 调用方据此告知客户端"无可用封面"，而不是发一个会拖垮队列的巨帧。
    /// </summary>
    public static async Task<byte[]?> EncodePngAsync(MediaInfo? media, CancellationToken cancellationToken)
    {
        if (media is null)
        {
            return null;
        }

        var bitmap = media.Thumbnail;
        if (bitmap is null && media.ThumbnailSource is not null)
        {
            bitmap = await media.ThumbnailSource.LoadBitmapAsync(false, cancellationToken);
        }

        if (bitmap is null)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        bitmap.Save(buffer, PngBitmapEncoderOptions.Default);
        if (buffer.Length == 0 || buffer.Length > MaxBytes)
        {
            return null;
        }

        return buffer.ToArray();
    }
}
