using System.Text;
using MediaIsland.Services.Lyrics.Crypto;
using MediaIsland.Services.Lyrics.Models;

namespace MediaIsland.Services.Lyrics;

/// <param name="Payload">解析成功时的歌词载荷；失败为 null。</param>
/// <param name="ErrorMessage">失败原因，直接展示给用户；成功为 null。</param>
internal sealed record LocalLyricsReadResult(LyricsPayload? Payload, string? ErrorMessage);

/// <summary>
/// 把本地歌词文件读成 <see cref="LyricsPayload"/>，交由现有解析器处理。
/// </summary>
/// <remarks>
/// 产物与在线来源同构，因此本地文件不是新的功能分支，只是固定歌词的另一个数据来源。
/// </remarks>
internal static class LocalLyricsFileReader
{
    public const long MaxFileSizeBytes = 5 * 1024 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static int _isCodePagesRegistered;

    public static LocalLyricsReadResult Read(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return new LocalLyricsReadResult(null, "找不到该歌词文件。");
        }

        var format = ResolveFormat(Path.GetExtension(filePath));
        if (format == LyricsFormat.Unknown)
        {
            return new LocalLyricsReadResult(null, "不支持的歌词文件格式，仅支持 .lrc / .qrc / .krc / .ttml。");
        }

        byte[] bytes;
        try
        {
            var info = new FileInfo(filePath);
            if (info.Length > MaxFileSizeBytes)
            {
                return new LocalLyricsReadResult(null, $"歌词文件超过 {MaxFileSizeBytes / 1024 / 1024} MB，已拒绝导入。");
            }

            bytes = File.ReadAllBytes(filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 不回显 ex.Message：它含完整路径，而 ProviderItemId 特意只记文件名就是为了不泄露用户名等个人信息。
            return new LocalLyricsReadResult(null, "读取歌词文件失败，请检查文件是否被占用或权限是否足够。");
        }

        if (bytes.Length == 0)
        {
            return new LocalLyricsReadResult(null, "歌词文件为空。");
        }

        var text = DecodeText(bytes);
        if (text is null)
        {
            return new LocalLyricsReadResult(null, "无法识别歌词文件的文本编码，请另存为 UTF-8 后重试。");
        }

        var content = Decrypt(format, text);
        if (string.IsNullOrWhiteSpace(content))
        {
            return new LocalLyricsReadResult(null, "歌词文件内容为空或无法解密。");
        }

        var fileName = Path.GetFileName(filePath);
        var payload = new LyricsPayload(
            format,
            content,
            LyricsSourceId.LocalFile,
            // 只记文件名：完整路径含用户名等个人信息，且文件移动后路径失效反而误导。
            $"file:{fileName}",
            new LyricsMetadata(Path.GetFileNameWithoutExtension(filePath), null, null, null));
        return new LocalLyricsReadResult(payload, null);
    }

    private static LyricsFormat ResolveFormat(string? extension) =>
        extension?.ToLowerInvariant() switch
        {
            ".lrc" => LyricsFormat.Lrc,
            ".qrc" => LyricsFormat.Qrc,
            ".krc" => LyricsFormat.Krc,
            // 不收 .xml：该扩展名过于宽泛，随机 XML 会被送进原生 TTML 解析器抛异常而非得到友好提示；
            // 实践中真正的 TTML 歌词文件都用 .ttml。
            ".ttml" => LyricsFormat.Ttml,
            _ => LyricsFormat.Unknown
        };

    /// <summary>
    /// 先看 BOM，再以严格 UTF-8 试解，失败回落 GBK——国内 .lrc 大量是 GBK，
    /// 用宽松 UTF-8 强解会静默产出乱码而不是报错。
    /// </summary>
    private static string? DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
        }

        try
        {
            EnsureCodePagesRegistered();
            return Encoding.GetEncoding("GBK").GetString(bytes);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? Decrypt(LyricsFormat format, string text)
    {
        var trimmed = text.Trim();
        return format switch
        {
            // QQ 音乐的 .qrc 可能是明文，也可能是 hex 密文。
            LyricsFormat.Qrc when LooksLikeHex(trimmed) => QrcDecrypter.Decrypt(trimmed) ?? trimmed,
            // 酷狗的 .krc 一律是 base64 包裹的加密体；解密失败则原样交给解析器。
            LyricsFormat.Krc => KrcDecrypter.Decrypt(trimmed) ?? trimmed,
            _ => text
        };
    }

    /// <summary>
    /// 判断 QRC 正文是否像 hex 密文。跳过空白字符与 <see cref="QrcDecrypter"/> 的 DecodeHex 保持一致——
    /// 密文换行分段很常见，若因内部空白判否，密文就会被当明文原样送进解析器。
    /// 保留这层预检是为了避免对大段明文 QRC 白做一次解密分配。
    /// </summary>
    private static bool LooksLikeHex(string value)
    {
        var hexCount = 0;
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c))
            {
                continue;
            }

            var isHex = c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!isHex)
            {
                return false;
            }

            hexCount++;
        }

        return hexCount >= 16 && hexCount % 2 == 0;
    }

    private static void EnsureCodePagesRegistered()
    {
        if (Interlocked.Exchange(ref _isCodePagesRegistered, 1) == 0)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
    }
}
