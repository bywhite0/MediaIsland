using MediaIsland.Services.Lyrics.Models;

namespace MediaIsland.Services.Lyrics.Storage;

/// <summary>
/// 落盘的歌词条目。存**原始 payload** 而非解析后的 <see cref="LyricsDocument"/>：
/// 昂贵的是 HTTP 往返而非解析，且解析器改进后老条目自动受益、逐字偏好变更无需清库。
/// </summary>
internal sealed record StoredLyrics(
    LyricsFormat Format,
    string Content,
    string? TranslationContent,
    string? RomanizationContent,
    LyricsSourceId Source,
    LyricsSourceId? OriginSource,
    string ProviderItemId,
    string? MetadataTitle,
    string? MetadataArtist,
    string? MetadataAlbum,
    long? MetadataDurationMs,
    string? Title,
    string? Artist,
    string? Album,
    long DurationMs,
    string SettingsFingerprint,
    DateTimeOffset SavedAtUtc,
    DateTimeOffset LastUsedAtUtc)
{
    public static StoredLyrics FromPayload(
        LyricsPayload payload,
        string? title,
        string? artist,
        string? album,
        TimeSpan duration,
        string settingsFingerprint,
        DateTimeOffset nowUtc) =>
        new(
            payload.Format,
            payload.Content,
            payload.TranslationContent,
            payload.RomanizationContent,
            payload.Source,
            OriginSource: null,
            payload.ProviderItemId,
            payload.Metadata.Title,
            payload.Metadata.Artist,
            payload.Metadata.Album,
            payload.Metadata.Duration is { } metadataDuration
                ? (long)metadataDuration.TotalMilliseconds
                : null,
            title,
            artist,
            album,
            (long)duration.TotalMilliseconds,
            settingsFingerprint,
            nowUtc,
            nowUtc);

    public LyricsPayload ToPayload() =>
        new(
            Format,
            Content,
            Source,
            ProviderItemId,
            new LyricsMetadata(
                MetadataTitle,
                MetadataArtist,
                MetadataAlbum,
                MetadataDurationMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null),
            TranslationContent,
            RomanizationContent);
}

/// <summary>索引条目：仅元信息，供 LRU 淘汰与设置页列表使用；丢失可从目录重建。</summary>
internal sealed record LyricsStoreIndexEntry(
    string Key,
    string? Title,
    string? Artist,
    string? Album,
    string SettingsFingerprint,
    DateTimeOffset LastUsedAtUtc,
    bool IsPinned,
    string Source);
