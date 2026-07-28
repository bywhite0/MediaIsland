using System.IO.Compression;
using System.Text;

namespace MediaIsland.Services.Lyrics.Crypto;

/// <summary>
/// Decrypts QQ Music QRC payloads: hex text -> triple DES -> zlib -> UTF-8.
/// </summary>
public static class QrcDecrypter
{
    /// <summary>Fixed 24-byte key shipped by the QQ Music client.</summary>
    private static readonly byte[] Key = Encoding.ASCII.GetBytes("!@#)(*$%123ZXC!@!@#)(NHL");

    /// <summary>The schedule is immutable once built, so a single instance can be shared.</summary>
    private static readonly QqMusicTripleDes Decryptor = QqMusicTripleDes.CreateDecryptor(Key);

    public static string? Decrypt(string? encrypted)
    {
        if (string.IsNullOrWhiteSpace(encrypted))
        {
            return null;
        }

        try
        {
            var payload = DecodeHex(encrypted);
            if (payload == null || payload.Length == 0 || payload.Length % QqMusicTripleDes.BlockSize != 0)
            {
                return null;
            }

            for (var offset = 0; offset < payload.Length; offset += QqMusicTripleDes.BlockSize)
            {
                var block = payload.AsSpan(offset, QqMusicTripleDes.BlockSize);
                Decryptor.TransformBlock(block, block);
            }

            return ZLibText.Inflate(payload, 0, payload.Length);
        }
        catch (Exception exception) when (exception is FormatException or InvalidDataException or ArgumentException)
        {
            return null;
        }
    }

    private static byte[]? DecodeHex(string value)
    {
        Span<char> buffer = value.Length <= 512 ? stackalloc char[value.Length] : new char[value.Length];
        var length = 0;
        foreach (var character in value)
        {
            if (!char.IsWhiteSpace(character))
            {
                buffer[length++] = character;
            }
        }

        return length % 2 == 0 ? Convert.FromHexString(buffer[..length]) : null;
    }
}
