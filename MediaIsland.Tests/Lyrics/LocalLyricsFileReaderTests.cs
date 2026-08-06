using System.Text;
using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Lyrics.Models;
using Xunit;

namespace MediaIsland.Tests.Lyrics;

public class LocalLyricsFileReaderTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "MediaIslandLocalLyricsTests",
        Guid.NewGuid().ToString("N"));

    public LocalLyricsFileReaderTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private string WriteFile(string name, string content, Encoding? encoding = null)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, content, encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private string WriteBytes(string name, byte[] bytes)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void Read_LrcFile_ProducesLrcPayload()
    {
        var path = WriteFile("song.lrc", "[00:01.00]Hello\n[00:02.00]World");

        var result = LocalLyricsFileReader.Read(path);

        Assert.Null(result.ErrorMessage);
        Assert.NotNull(result.Payload);
        Assert.Equal(LyricsFormat.Lrc, result.Payload!.Format);
        Assert.Equal(LyricsSourceId.LocalFile, result.Payload.Source);
        Assert.Contains("Hello", result.Payload.Content, StringComparison.Ordinal);
    }

    /// <summary>ProviderItemId 只记文件名：完整路径含用户名等个人信息，且文件移动后失效。</summary>
    [Fact]
    public void Read_ProviderItemId_ContainsOnlyFileName()
    {
        var path = WriteFile("song.lrc", "[00:01.00]Hello");

        var result = LocalLyricsFileReader.Read(path);

        Assert.Equal("file:song.lrc", result.Payload!.ProviderItemId);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, result.Payload.ProviderItemId);
    }

    [Fact]
    public void Read_TtmlFile_ProducesTtmlPayload()
    {
        var path = WriteFile(
            "song.ttml",
            """<tt xmlns="http://www.w3.org/ns/ttml"><body><div><p begin="0s" end="1s">Hi</p></div></body></tt>""");

        var result = LocalLyricsFileReader.Read(path);

        Assert.Null(result.ErrorMessage);
        Assert.Equal(LyricsFormat.Ttml, result.Payload!.Format);
    }

    [Fact]
    public void Read_PlaintextQrcFile_ProducesQrcPayload()
    {
        var path = WriteFile("song.qrc", "[0,1000]Hello(0,500) world(500,500)");

        var result = LocalLyricsFileReader.Read(path);

        Assert.Null(result.ErrorMessage);
        Assert.Equal(LyricsFormat.Qrc, result.Payload!.Format);
        Assert.StartsWith("[0,1000]", result.Payload.Content, StringComparison.Ordinal);
    }

    /// <summary>QRC 也可能是 hex 密文，需要解密后再交给解析器。</summary>
    [Fact]
    public void Read_EncryptedQrcFile_IsDecrypted()
    {
        // brief 内嵌的 hex 常量与实际解密结果不符（多了前缀块且拼接了 CJK 尾部），
        // 按 team-lead 指示改用 LyricsDecrypterTests 里既有的 QrcAsciiPayload。
        const string encrypted =
            "9523D140F2F5DC811B9D5061A37442A82BC5E65F361C6B30D5695555DAEB969B38E6B8AECDA8CB04EF8AAEB13756B3F0";
        var path = WriteFile("song.qrc", encrypted);

        var result = LocalLyricsFileReader.Read(path);

        Assert.Null(result.ErrorMessage);
        Assert.Equal(LyricsFormat.Qrc, result.Payload!.Format);
        Assert.Equal("[0,1000]Hello(0,500) world(500,500)", result.Payload.Content);
    }

    [Fact]
    public void Read_KrcFile_IsDecrypted()
    {
        const string plaintext = "[1000,1000]<0,500,0>Hello<500,500,0> world";
        var path = WriteFile("song.krc", BuildKrcPayload(plaintext));

        var result = LocalLyricsFileReader.Read(path);

        Assert.Null(result.ErrorMessage);
        Assert.Equal(LyricsFormat.Krc, result.Payload!.Format);
        Assert.Equal(plaintext, result.Payload.Content);
    }

    [Fact]
    public void Read_Utf8WithBom_StripsPreamble()
    {
        var path = WriteFile(
            "song.lrc",
            "[00:01.00]晴天",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var result = LocalLyricsFileReader.Read(path);

        Assert.Null(result.ErrorMessage);
        Assert.StartsWith("[00:01.00]", result.Payload!.Content, StringComparison.Ordinal);
        Assert.Contains("晴天", result.Payload.Content, StringComparison.Ordinal);
    }

    /// <summary>国内 .lrc 大量是 GBK，UTF-8 强解会变乱码。</summary>
    [Fact]
    public void Read_GbkFile_IsDecodedCorrectly()
    {
        Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var gbk = Encoding.GetEncoding("GBK");
        var path = WriteBytes("song.lrc", gbk.GetBytes("[00:01.00]晴天娃娃"));

        var result = LocalLyricsFileReader.Read(path);

        Assert.Null(result.ErrorMessage);
        Assert.Contains("晴天娃娃", result.Payload!.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_EmptyFile_ReportsError()
    {
        var path = WriteFile("song.lrc", "");

        var result = LocalLyricsFileReader.Read(path);

        Assert.Null(result.Payload);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public void Read_WhitespaceOnlyFile_ReportsError()
    {
        var path = WriteFile("song.lrc", "   \n\n  ");

        var result = LocalLyricsFileReader.Read(path);

        Assert.Null(result.Payload);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public void Read_MissingFile_ReportsError()
    {
        var result = LocalLyricsFileReader.Read(Path.Combine(_folder, "nope.lrc"));

        Assert.Null(result.Payload);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public void Read_UnsupportedExtension_ReportsError()
    {
        var path = WriteFile("song.txt", "[00:01.00]Hello");

        var result = LocalLyricsFileReader.Read(path);

        Assert.Null(result.Payload);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public void Read_OversizedFile_IsRejected()
    {
        var path = Path.Combine(_folder, "big.lrc");
        using (var stream = File.Create(path))
        {
            stream.SetLength(LocalLyricsFileReader.MaxFileSizeBytes + 1);
        }

        var result = LocalLyricsFileReader.Read(path);

        Assert.Null(result.Payload);
        Assert.NotNull(result.ErrorMessage);
    }

    /// <summary>与 LyricsDecrypterTests.BuildKrcPayload 一致：base64("krc1" + XOR(zlib(text)))。</summary>
    private static string BuildKrcPayload(string plaintext)
    {
        var key = new byte[]
        {
            0x40, 0x47, 0x61, 0x77, 0x5E, 0x32, 0x74, 0x47,
            0x51, 0x36, 0x31, 0x2D, 0xCE, 0xD2, 0x6E, 0x69
        };
        using var compressed = new MemoryStream();
        using (var deflate = new System.IO.Compression.ZLibStream(
                   compressed,
                   System.IO.Compression.CompressionMode.Compress,
                   leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(plaintext);
            deflate.Write(bytes, 0, bytes.Length);
        }

        var body = compressed.ToArray();
        for (var i = 0; i < body.Length; i++)
        {
            body[i] ^= key[i % key.Length];
        }

        var payload = new byte[4 + body.Length];
        "krc1"u8.CopyTo(payload);
        body.CopyTo(payload, 4);
        return Convert.ToBase64String(payload);
    }
}
