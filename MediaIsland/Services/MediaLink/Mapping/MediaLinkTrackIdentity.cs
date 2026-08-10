namespace MediaIsland.Services.MediaLink.Mapping;

/// <summary>
/// 曲目标识四字段的规范化。
///
/// 存在的理由是 token 的跨机相等性：发送端从本机媒体信息算 token，接收端从注入存储
/// 算 token，而注入存储在写入时会削首尾空白、把空值折叠为 null。两处不共用规则时，
/// 带空白的元数据会让接收端把全部音频帧判为过期曲目并静默丢弃——不抛异常、不打日志，
/// 只表现为「连着上游但频谱不动」。
///
/// 规范化必须幂等：中继链上每一跳都会再走一次，不幂等则链路越长越容易失配。
/// </summary>
internal static class MediaLinkTrackIdentity
{
    /// <summary>来源缺失时的占位。与注入存储的既有取值保持一致。</summary>
    internal const string DefaultSourceApp = "external";

    internal readonly record struct Normalized(
        string SourceApp,
        string? Title,
        string? Artist,
        string? AlbumTitle);

    internal static Normalized Normalize(
        string? sourceApp, string? title, string? artist, string? albumTitle) =>
        new(
            string.IsNullOrWhiteSpace(sourceApp) ? DefaultSourceApp : sourceApp.Trim(),
            NormalizeOptional(title),
            NormalizeOptional(artist),
            NormalizeOptional(albumTitle));

    /// <summary>空、空白与 null 三者同义：都表示「这个字段没有内容」。</summary>
    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
