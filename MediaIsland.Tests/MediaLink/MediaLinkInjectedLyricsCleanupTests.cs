using MediaIsland.Services.Lyrics.Cleanup;
using MediaIsland.Services.Lyrics.Models;
using MediaIsland.Services.MediaLink;
using MediaIsland.Services.MediaLink.Mapping;
using MediaIsland.Services.MediaLink.Protocol;
using Xunit;

namespace MediaIsland.Tests.MediaLink;

public class MediaLinkInjectedLyricsCleanupTests
{
    private static readonly LyricsCleanupOptions StripAndMask =
        new() { StripCredits = true, MaskEnabled = true, MaskWords = ["坏词"] };

    private static MediaLinkLyricsDto NewPayload(params string[] texts) => new()
    {
        Id = "ly-1",
        Title = "春风十里",
        Artist = "鹿先森乐队",
        DurationMs = 120_000,
        Document = new MediaLinkLyricsDocumentDto
        {
            Format = "Lrc",
            ProviderItemId = "item-1",
            Lines = texts.Select((text, i) => new MediaLinkLyricsLineDto
            {
                StartMs = i * 1000,
                EndMs = i * 1000 + 900,
                Text = text
            }).ToList()
        }
    };

    [Fact]
    public void TryMapInjectedLyrics_WithCleanup_StripsAndMasks()
    {
        var payload = NewPayload("春风十里 - 鹿先森乐队", "作词：青石", "有个坏词", "第二句");

        Assert.True(MediaLinkDtoMapper.TryMapInjectedLyrics(payload, out var result, out _, StripAndMask));

        Assert.Equal(["有个**", "第二句"], result!.Document.Lines.Select(l => l.Text));
        Assert.Equal(LyricsSourceId.External, result.Source);
    }

    [Fact]
    public void TryMapInjectedLyrics_WithoutCleanup_KeepsEverything()
    {
        var payload = NewPayload("作词：青石", "有个坏词");

        Assert.True(MediaLinkDtoMapper.TryMapInjectedLyrics(payload, out var result, out _));

        Assert.Equal(["作词：青石", "有个坏词"], result!.Document.Lines.Select(l => l.Text));
    }

    [Fact]
    public void TryMapInjectedLyrics_AllLinesRemoved_InfersUnsyncedFromRemainingLines()
    {
        var payload = NewPayload("作词：青石");

        Assert.True(MediaLinkDtoMapper.TryMapInjectedLyrics(payload, out var result, out _, StripAndMask));

        Assert.Empty(result!.Document.Lines);
        Assert.Equal(LyricsSyncMode.Unsynced, result.Document.SyncMode);
    }

    [Fact]
    public void InjectionStore_TrySetLyrics_UsesCurrentRulesAtInjectionTime()
    {
        LyricsCleanupOptions? current = null;
        var store = new MediaLinkInjectionStore(cleanupProvider: () => current);

        Assert.True(store.TrySetLyrics(NewPayload("作词：青石", "第一句"), out _));
        var before = store.GetLyricsSnapshot()!.Document.Lines.Count;
        current = StripAndMask;
        Assert.True(store.TrySetLyrics(NewPayload("作词：青石", "第一句"), out _));
        var after = store.GetLyricsSnapshot()!.Document.Lines.Select(l => l.Text);

        Assert.Equal(2, before);
        Assert.Equal(["第一句"], after);
    }

    [Fact]
    public void InjectionStore_DefaultConstructor_DoesNotClean()
    {
        var store = new MediaLinkInjectionStore();

        Assert.True(store.TrySetLyrics(NewPayload("作词：青石", "第一句"), out _));

        Assert.Equal(2, store.GetLyricsSnapshot()!.Document.Lines.Count);
    }
}
