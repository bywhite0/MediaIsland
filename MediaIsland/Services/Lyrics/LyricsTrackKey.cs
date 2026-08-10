using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MediaIsland.Services.Lyrics;

/// <summary>
/// 跨播放器稳定的曲目标识，用作歌词缓存与固定歌词的键。
/// </summary>
/// <remarks>
/// 与 <c>MediaLinkDtoMapper.ComputeTrackToken</c> 哈希构造相同（SHA256、U+001F 分隔、取前 8 字节），
/// 但入哈希前的归一化力度不同，故不共用代码：后者把 SourceApp 计入哈希，且只削首尾空白、保留大小写与
/// 字段内部的空白，用于让客户端把 media 与 lyrics 两帧配对——那只要求同一首曲目在链路两端算得一致；
/// 本类要求同一首歌在任何播放器下都得到同一个键，因此不含 SourceApp，并进一步转小写、去掉全部空白。
/// 时长同样不参与——播放器上报的时长有零点几秒的抖动，计入会让缓存永久不命中。
/// </remarks>
internal static partial class LyricsTrackKey
{
    /// <summary>分隔符用 U+001F，避免字段内容拼接产生歧义。</summary>
    private const char FieldSeparator = '\x1F';

    public static string Compute(string? title, string? artist, string? album)
    {
        var key = string.Join(
            FieldSeparator,
            NormalizeField(title),
            NormalizeField(artist),
            NormalizeField(album));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    /// <summary>
    /// 归一化单个字段：小写、去掉分隔符与全部空白。
    /// <see cref="LyricsTextNormalizer.NormalizeComparableText"/> 只把分隔符折叠成空格，
    /// 因此这里再去掉全部空白，确保 "Le-mon" 与 "Lemon" 得到同一个键——
    /// 字段间的歧义由 <see cref="FieldSeparator"/> 保证，不受此影响。
    /// 用显式的空白正则而非 <c>Replace(" ", "")</c>，以免依赖上游把全角空格转半角的实现细节。
    /// </summary>
    private static string NormalizeField(string? text) =>
        WhitespaceRegex().Replace(
            LyricsTextNormalizer.NormalizeComparableText(text).ToLowerInvariant(),
            string.Empty);

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}
