using MediaIsland.Services.Lyrics.Models;
using MediaIsland.Services.Lyrics.Storage;
using Xunit;

namespace MediaIsland.Tests.Lyrics;

public class StoredLyricsTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    private static LyricsPayload CreatePayload() => new(
        LyricsFormat.Qrc,
        "[0,1000]Hello(0,500) world(500,500)",
        LyricsSourceId.QqMusic,
        "song-1",
        new LyricsMetadata("Lemon", "米津玄師", "Lemon", TimeSpan.FromSeconds(256)),
        TranslationContent: "[00:00.00]你好",
        RomanizationContent: "[00:00.00]nihao");

    [Fact]
    public void FromPayload_ThenToPayload_RoundTrips()
    {
        var original = CreatePayload();

        var restored = StoredLyrics
            .FromPayload(original, "Lemon", "米津玄師", "Lemon", TimeSpan.FromSeconds(256), "fp-1", Now)
            .ToPayload();

        Assert.Equal(original.Format, restored.Format);
        Assert.Equal(original.Content, restored.Content);
        Assert.Equal(original.Source, restored.Source);
        Assert.Equal(original.ProviderItemId, restored.ProviderItemId);
        Assert.Equal(original.TranslationContent, restored.TranslationContent);
        Assert.Equal(original.RomanizationContent, restored.RomanizationContent);
        Assert.Equal(original.Metadata, restored.Metadata);
    }

    [Fact]
    public void FromPayload_CapturesTrackAndBookkeepingFields()
    {
        var stored = StoredLyrics.FromPayload(
            CreatePayload(), "Lemon", "米津玄師", "Lemon", TimeSpan.FromSeconds(256), "fp-1", Now);

        Assert.Equal("Lemon", stored.Title);
        Assert.Equal("米津玄師", stored.Artist);
        Assert.Equal("Lemon", stored.Album);
        Assert.Equal(256_000, stored.DurationMs);
        Assert.Equal("fp-1", stored.SettingsFingerprint);
        Assert.Equal(Now, stored.SavedAtUtc);
        Assert.Equal(Now, stored.LastUsedAtUtc);
    }

    /// <summary>落盘的是原始 payload 而非解析后的 document——解析器改进后老缓存自动受益。</summary>
    [Fact]
    public void StoredLyrics_SerializesToJsonAndBack()
    {
        var stored = StoredLyrics.FromPayload(
            CreatePayload(), "Lemon", "米津玄師", "Lemon", TimeSpan.FromSeconds(256), "fp-1", Now);

        var json = System.Text.Json.JsonSerializer.Serialize(stored);
        var restored = System.Text.Json.JsonSerializer.Deserialize<StoredLyrics>(json);

        Assert.NotNull(restored);
        Assert.Equal(stored, restored);
    }

    [Fact]
    public void ToPayload_RestoresNullSecondaryTracks()
    {
        var payload = new LyricsPayload(
            LyricsFormat.Lrc,
            "[00:00.00]Hello",
            LyricsSourceId.Netease,
            "song-2",
            new LyricsMetadata("A", "B", null, null));

        var restored = StoredLyrics
            .FromPayload(payload, "A", "B", null, TimeSpan.Zero, "fp", Now)
            .ToPayload();

        Assert.Null(restored.TranslationContent);
        Assert.Null(restored.RomanizationContent);
        Assert.Null(restored.Metadata.Album);
        Assert.Null(restored.Metadata.Duration);
    }
}
