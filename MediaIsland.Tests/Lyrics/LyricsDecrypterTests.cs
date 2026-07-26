using System.IO.Compression;
using System.Text;
using MediaIsland.Services.Lyrics.Crypto;
using Xunit;

namespace MediaIsland.Tests.Lyrics;

public class LyricsDecrypterTests
{
    // Produced by the QQ Music QRC pipeline (zlib + triple DES + hex) for the plaintext asserted below.
    private const string QrcAsciiPayload =
        "9523D140F2F5DC811B9D5061A37442A82BC5E65F361C6B30D5695555DAEB969B38E6B8AECDA8CB04EF8AAEB13756B3F0";

    private const string QrcCjkPayload =
        "9523D140F2F5DC818719EFDB66E8B45ED4D4F715310896AA84F8BDD8F212060EDBF9A9F53C9BC214DBC4C97999B94C0A";

    [Fact]
    public void QrcDecrypter_AsciiPayload_ReturnsPlainText()
    {
        Assert.Equal(
            "[0,1000]Hello(0,500) world(500,500)",
            QrcDecrypter.Decrypt(QrcAsciiPayload));
    }

    [Fact]
    public void QrcDecrypter_MultiBytePayload_ReturnsPlainText()
    {
        Assert.Equal(
            "[0,1200]晴天(0,600)娃娃(600,600)",
            QrcDecrypter.Decrypt(QrcCjkPayload));
    }

    [Fact]
    public void QrcDecrypter_StripsUtf8Preamble()
    {
        var decrypted = QrcDecrypter.Decrypt(QrcAsciiPayload);

        Assert.NotNull(decrypted);
        Assert.StartsWith("[0,1000]", decrypted, StringComparison.Ordinal);
        Assert.DoesNotContain('﻿', decrypted);
    }

    [Fact]
    public void QrcDecrypter_LowercaseHex_IsAccepted()
    {
        Assert.Equal(
            QrcDecrypter.Decrypt(QrcAsciiPayload),
            QrcDecrypter.Decrypt(QrcAsciiPayload.ToLowerInvariant()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ABC")]              // odd length
    [InlineData("AABBCC")]           // not a whole 8-byte block
    [InlineData("ZZZZZZZZZZZZZZZZ")] // not hex
    [InlineData("00112233445566778899AABBCCDDEEFF")] // hex, but not a valid zlib stream after decryption
    public void QrcDecrypter_InvalidInput_ReturnsNull(string? encrypted)
    {
        Assert.Null(QrcDecrypter.Decrypt(encrypted));
    }

    [Fact]
    public void KrcDecrypter_ValidPayload_ReturnsPlainText()
    {
        const string plaintext = "[id:$00000000]\n[ar:Artist]\n[1000,1000]<0,500,0>Hello<500,500,0> world";

        Assert.Equal(plaintext, KrcDecrypter.Decrypt(BuildKrcPayload(plaintext)));
    }

    [Fact]
    public void KrcDecrypter_MultiBytePayload_ReturnsPlainText()
    {
        const string plaintext = "[500,1500]<0,700,0>晴天<700,800,0>娃娃";

        Assert.Equal(plaintext, KrcDecrypter.Decrypt(BuildKrcPayload(plaintext)));
    }

    [Fact]
    public void KrcDecrypter_StripsUtf8Preamble()
    {
        var decrypted = KrcDecrypter.Decrypt(BuildKrcPayload("[0,10]<0,10,0>a"));

        Assert.NotNull(decrypted);
        Assert.StartsWith("[0,10]", decrypted, StringComparison.Ordinal);
        Assert.DoesNotContain('﻿', decrypted);
    }

    [Fact]
    public void KrcDecrypter_PayloadWithoutPreamble_KeepsFirstCharacter()
    {
        const string plaintext = "[0,10]<0,10,0>a";

        Assert.Equal(plaintext, KrcDecrypter.Decrypt(BuildKrcPayload(plaintext, withPreamble: false)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!not base64!!!")]
    public void KrcDecrypter_InvalidInput_ReturnsNull(string? encrypted)
    {
        Assert.Null(KrcDecrypter.Decrypt(encrypted));
    }

    [Fact]
    public void KrcDecrypter_TooShortPayload_ReturnsNull()
    {
        Assert.Null(KrcDecrypter.Decrypt(Convert.ToBase64String("krc"u8.ToArray())));
    }

    [Fact]
    public void KrcDecrypter_CorruptedBody_ReturnsNull()
    {
        var payload = Convert.FromBase64String(BuildKrcPayload("[0,10]<0,10,0>a"));
        payload[^1] ^= 0xFF;
        payload[^2] ^= 0xFF;

        Assert.Null(KrcDecrypter.Decrypt(Convert.ToBase64String(payload)));
    }

    /// <summary>Builds a Kugou-shaped payload: base64("krc1" + XOR(zlib(text))).</summary>
    private static string BuildKrcPayload(string plaintext, bool withPreamble = true)
    {
        byte[] key =
        [
            0x40, 0x47, 0x61, 0x77, 0x5E, 0x32, 0x74, 0x47,
            0x51, 0x36, 0x31, 0x2D, 0xCE, 0xD2, 0x6E, 0x69
        ];

        var body = withPreamble
            ? [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(plaintext)]
            : Encoding.UTF8.GetBytes(plaintext);

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(body, 0, body.Length);
        }

        var encrypted = compressed.ToArray();
        for (var i = 0; i < encrypted.Length; i++)
        {
            encrypted[i] ^= key[i % key.Length];
        }

        return Convert.ToBase64String([.. "krc1"u8, .. encrypted]);
    }
}
