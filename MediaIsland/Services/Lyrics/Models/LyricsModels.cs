using MediaIsland.Services.Media;

namespace MediaIsland.Services.Lyrics.Models;

public enum LyricsSyncMode
{
    Unsynced,
    Line,
    Word
}

public enum LyricsFormat
{
    Unknown,
    Lrc,
    Qrc,
    Krc,
    Ttml
}

public enum LyricsSourceId
{
    Netease,
    QqMusic,
    Kugou,
    AmllTtml
}

public sealed record LyricsMetadata(
    string? Title,
    string? Artist,
    string? Album,
    TimeSpan? Duration);

public sealed record LyricsWord(
    TimeSpan StartTime,
    TimeSpan EndTime,
    string Text);

/// <summary>
/// 字级注音片段，锚定在 <see cref="LyricsLine.Text"/> 的 UTF-16 区间上。
/// </summary>
/// <param name="BaseStart">原文起点（UTF-16 索引）。</param>
/// <param name="BaseLength">覆盖的原文码元长度；多字注音时通常大于 1。</param>
/// <param name="Reading">假名读音；不应包含 QRC 内嵌 timing。</param>
public sealed record LyricsRubySpan(
    int BaseStart,
    int BaseLength,
    string Reading);

public sealed record LyricsLine(
    TimeSpan StartTime,
    TimeSpan EndTime,
    string Text,
    IReadOnlyList<LyricsWord> Words,
    string? Translation = null,
    string? Romanization = null,
    bool IsBackground = false,
    bool IsDuet = false,
    IReadOnlyList<LyricsRubySpan>? RubySpans = null);

public sealed record LyricsDocument(
    LyricsMetadata Metadata,
    IReadOnlyList<LyricsLine> Lines,
    LyricsSyncMode SyncMode,
    LyricsSourceId Source,
    string ProviderItemId,
    LyricsFormat Format);

public sealed record LyricsCandidate(
    LyricsSourceId Source,
    string ProviderItemId,
    string Title,
    string Artist,
    string Album,
    TimeSpan Duration,
    int Score,
    bool SupportsWordSync,
    IReadOnlyDictionary<string, string>? Extra = null);

public sealed record LyricsPayload(
    LyricsFormat Format,
    string Content,
    LyricsSourceId Source,
    string ProviderItemId,
    LyricsMetadata Metadata,
    string? TranslationContent = null,
    string? RomanizationContent = null);

public sealed class LyricsSourceEntry
{
    public LyricsSourceId Id { get; set; } = LyricsSourceId.Netease;
    public bool IsEnabled { get; set; } = true;
    public bool UseWordSyncedLyrics { get; set; } = true;
    public int GlobalOffsetMilliseconds { get; set; }
}

public sealed class LyricsSourceSettings
{
    public List<LyricsSourceEntry> Sources { get; set; } = CreateDefaultSources();

    public string AmllApiBaseUrl { get; set; } = string.Empty;

    public static List<LyricsSourceEntry> CreateDefaultSources() =>
    [
        new() { Id = LyricsSourceId.AmllTtml, IsEnabled = false, UseWordSyncedLyrics = true },
        new() { Id = LyricsSourceId.QqMusic, IsEnabled = true, UseWordSyncedLyrics = true },
        new() { Id = LyricsSourceId.Kugou, IsEnabled = true, UseWordSyncedLyrics = true },
        new() { Id = LyricsSourceId.Netease, IsEnabled = true, UseWordSyncedLyrics = false }
    ];

    public LyricsSourceSettings Clone()
    {
        return new LyricsSourceSettings
        {
            AmllApiBaseUrl = AmllApiBaseUrl,
            Sources = (Sources ?? []).OfType<LyricsSourceEntry>().Select(source => new LyricsSourceEntry
            {
                Id = source.Id,
                IsEnabled = source.IsEnabled,
                UseWordSyncedLyrics = source.UseWordSyncedLyrics,
                GlobalOffsetMilliseconds = source.GlobalOffsetMilliseconds
            }).ToList()
        };
    }

    public static LyricsSourceSettings Normalize(LyricsSourceSettings? settings)
    {
        settings ??= new LyricsSourceSettings();
        var defaults = CreateDefaultSources();
        var seen = new HashSet<LyricsSourceId>();
        var normalized = new List<LyricsSourceEntry>();

        foreach (var source in (settings.Sources ?? []).OfType<LyricsSourceEntry>())
        {
            if (!Enum.IsDefined(typeof(LyricsSourceId), source.Id) || !seen.Add(source.Id))
            {
                continue;
            }

            normalized.Add(new LyricsSourceEntry
            {
                Id = source.Id,
                IsEnabled = source.IsEnabled,
                UseWordSyncedLyrics = source.UseWordSyncedLyrics,
                GlobalOffsetMilliseconds = source.GlobalOffsetMilliseconds
            });
        }

        foreach (var source in defaults)
        {
            if (seen.Add(source.Id))
            {
                normalized.Add(new LyricsSourceEntry
                {
                    Id = source.Id,
                    IsEnabled = source.IsEnabled,
                    UseWordSyncedLyrics = source.UseWordSyncedLyrics,
                    GlobalOffsetMilliseconds = source.GlobalOffsetMilliseconds
                });
            }
        }

        settings.Sources = normalized;
        settings.AmllApiBaseUrl = NormalizeAmllBaseUrl(settings.AmllApiBaseUrl);
        return settings;
    }

    public static string NormalizeAmllBaseUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return string.Empty;
        }

        return trimmed;
    }

    public bool IsSourceEnabled(LyricsSourceId id) =>
        Sources.FirstOrDefault(source => source.Id == id)?.IsEnabled == true;

    public bool PreferWordSync(LyricsSourceId id) =>
        Sources.FirstOrDefault(source => source.Id == id)?.UseWordSyncedLyrics == true;

    public TimeSpan GetGlobalOffset(LyricsSourceId id) =>
        TimeSpan.FromMilliseconds(Sources.FirstOrDefault(source => source.Id == id)?.GlobalOffsetMilliseconds ?? 0);
}
