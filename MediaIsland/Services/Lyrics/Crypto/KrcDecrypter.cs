using System.IO.Compression;

namespace MediaIsland.Services.Lyrics.Crypto;

/// <summary>
/// Decrypts Kugou KRC payloads: base64 -> drop the 4-byte "krc1" magic -> repeating-key XOR -> zlib -> UTF-8.
/// </summary>
public static class KrcDecrypter
{
    /// <summary>Fixed XOR key shipped by the Kugou client.</summary>
    private static readonly byte[] Key =
    [
        0x40, 0x47, 0x61, 0x77, 0x5E, 0x32, 0x74, 0x47,
        0x51, 0x36, 0x31, 0x2D, 0xCE, 0xD2, 0x6E, 0x69
    ];

    private const int MagicLength = 4;

    public static string? Decrypt(string? encrypted)
    {
        if (string.IsNullOrWhiteSpace(encrypted))
        {
            return null;
        }

        try
        {
            var payload = Convert.FromBase64String(encrypted);
            if (payload.Length <= MagicLength)
            {
                return null;
            }

            for (var i = MagicLength; i < payload.Length; i++)
            {
                payload[i] ^= Key[(i - MagicLength) % Key.Length];
            }

            return ZLibText.Inflate(payload, MagicLength, payload.Length - MagicLength);
        }
        catch (Exception exception) when (exception is FormatException or InvalidDataException)
        {
            return null;
        }
    }
}
