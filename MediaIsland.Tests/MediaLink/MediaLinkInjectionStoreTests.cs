using MediaIsland.Services.MediaLink;
using MediaIsland.Services.MediaLink.Protocol;
using Xunit;

namespace MediaIsland.Tests.MediaLink;

public class MediaLinkInjectionStoreTests
{
    [Fact]
    public void SetMedia_Lww_And_RequiresTitle()
    {
        var store = new MediaLinkInjectionStore();
        Assert.False(store.TrySetMedia(new MediaLinkMediaInjectPayload { Title = " " }, out var err));
        Assert.Contains("title", err, StringComparison.OrdinalIgnoreCase);

        Assert.True(store.TrySetMedia(new MediaLinkMediaInjectPayload
        {
            Title = "A",
            PositionMs = 0,
            DurationMs = 10_000,
            PlaybackState = "Paused"
        }, out _));
        Assert.Equal("A", store.GetMediaSnapshot()!.Title);

        Assert.True(store.TrySetMedia(new MediaLinkMediaInjectPayload
        {
            Title = "B",
            PositionMs = 1000,
            DurationMs = 10_000,
            PlaybackState = "Playing",
            PlaybackRate = 1.0
        }, out _));
        Assert.Equal("B", store.GetMediaSnapshot()!.Title);
    }

    [Fact]
    public void NegativeMs_And_UnknownPlaybackState_BadRequest()
    {
        var store = new MediaLinkInjectionStore();
        Assert.False(store.TrySetMedia(new MediaLinkMediaInjectPayload
        {
            Title = "T",
            PositionMs = -1,
            DurationMs = 1,
            PlaybackState = "Playing"
        }, out _));
        Assert.False(store.TrySetMedia(new MediaLinkMediaInjectPayload
        {
            Title = "T",
            PositionMs = 0,
            DurationMs = 1,
            PlaybackState = "NotAState"
        }, out _));
    }

    [Fact]
    public void VirtualPlayPause_PositionAdvancesOnlyWhilePlaying()
    {
        var tick = 1_000L;
        var store = new MediaLinkInjectionStore(tickProvider: () => tick);
        Assert.True(store.TrySetMedia(new MediaLinkMediaInjectPayload
        {
            Title = "T",
            PositionMs = 5_000,
            DurationMs = 60_000,
            PlaybackState = "Paused",
            PlaybackRate = 1.0
        }, out _));

        var p0 = store.GetMediaSnapshot()!.Position;
        tick += 2_000;
        Assert.Equal(p0, store.GetMediaSnapshot()!.Position);

        Assert.True(store.TryVirtualPlay(out _));
        tick += 2_000;
        Assert.True(store.GetMediaSnapshot()!.Position > p0);

        Assert.True(store.TryVirtualPause(out _));
        var pausedAt = store.GetMediaSnapshot()!.Position;
        tick += 5_000;
        Assert.Equal(pausedAt, store.GetMediaSnapshot()!.Position);
    }

    [Fact]
    public void Clear_Channels_Independent()
    {
        var store = new MediaLinkInjectionStore();
        Assert.True(store.TrySetMedia(new MediaLinkMediaInjectPayload
        {
            Title = "T", PlaybackState = "Paused"
        }, out _));
        Assert.True(store.TrySetLyrics(new MediaLinkLyricsDto
        {
            Title = "T",
            Source = "External",
            Document = new MediaLinkLyricsDocumentDto { Lines = [] }
        }, out _));

        Assert.True(store.TryClear(["media"], out _));
        Assert.Null(store.GetMediaSnapshot());
        Assert.NotNull(store.GetLyricsSnapshot());

        Assert.True(store.TryClear(null, out _));
        Assert.Null(store.GetLyricsSnapshot());
    }

    [Fact]
    public void Clear_UnknownChannel_Fails()
    {
        var store = new MediaLinkInjectionStore();
        Assert.False(store.TryClear(["thumb"], out var err));
        Assert.Contains("channel", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VirtualPlay_WithoutMedia_Fails()
    {
        var store = new MediaLinkInjectionStore();
        Assert.False(store.TryVirtualPlay(out _));
    }
}
