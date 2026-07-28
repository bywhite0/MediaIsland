using System.IO.Compression;
using System.Text;

namespace MediaIsland.Services.Lyrics.Crypto;

/// <summary>
/// Shared zlib-to-text step for the QRC and KRC decrypters.
/// </summary>
internal static class ZLibText
{
    /// <summary>
    /// Upper bound for a decompressed lyric body. Payloads come from untrusted third-party
    /// endpoints, so a malformed or hostile stream must not be able to expand without limit.
    /// </summary>
    private const int MaxDecompressedBytes = 8 * 1024 * 1024;

    /// <summary>Inflates a zlib stream and decodes it as UTF-8, dropping a leading BOM.</summary>
    /// <returns>The decoded text, or <c>null</c> when the payload exceeds the size limit.</returns>
    public static string? Inflate(byte[] data, int offset, int count)
    {
        using var source = new MemoryStream(data, offset, count, writable: false);
        using var zlib = new ZLibStream(source, CompressionMode.Decompress);
        using var destination = new MemoryStream();

        var buffer = new byte[16 * 1024];
        int read;
        while ((read = zlib.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (destination.Length + read > MaxDecompressedBytes)
            {
                return null;
            }

            destination.Write(buffer, 0, read);
        }

        var decompressed = destination.GetBuffer().AsSpan(0, (int)destination.Length);
        var preamble = Encoding.UTF8.GetPreamble();
        if (decompressed.StartsWith(preamble))
        {
            decompressed = decompressed[preamble.Length..];
        }

        return Encoding.UTF8.GetString(decompressed);
    }
}
