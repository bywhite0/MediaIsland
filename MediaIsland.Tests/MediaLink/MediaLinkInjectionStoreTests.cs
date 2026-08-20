using MediaIsland.Services.Media;
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
    public void PositionAgeMs_BackdatesBaseline_SoPositionIncludesElapsedTime()
    {
        // 转发场景：上游采样位置 5000ms，经传输与处理 800ms 后才注入。
        // 不回补这 800ms，转发链上每跳都会让进度落后一次。
        var tick = 10_000L;
        var store = new MediaLinkInjectionStore(tickProvider: () => tick);

        Assert.True(store.TrySetMedia(new MediaLinkMediaInjectPayload
        {
            Title = "T",
            PositionMs = 5_000,
            DurationMs = 60_000,
            PlaybackState = "Playing",
            PlaybackRate = 1.0,
            PositionAgeMs = 800
        }, out _));

        Assert.Equal(TimeSpan.FromMilliseconds(5_800), store.GetMediaSnapshot()!.Position);
    }

    [Fact]
    public void PositionAgeMs_Omitted_TreatsPositionAsCurrent()
    {
        // 未携带该字段的旧客户端行为不变。
        var tick = 10_000L;
        var store = new MediaLinkInjectionStore(tickProvider: () => tick);

        Assert.True(store.TrySetMedia(new MediaLinkMediaInjectPayload
        {
            Title = "T",
            PositionMs = 5_000,
            DurationMs = 60_000,
            PlaybackState = "Playing",
            PlaybackRate = 1.0
        }, out _));

        Assert.Equal(TimeSpan.FromMilliseconds(5_000), store.GetMediaSnapshot()!.Position);
    }

    [Fact]
    public void PositionAgeMs_IgnoredWhilePaused()
    {
        // 暂停时位置不随时间前进，补偿也就无从谈起。
        var tick = 10_000L;
        var store = new MediaLinkInjectionStore(tickProvider: () => tick);

        Assert.True(store.TrySetMedia(new MediaLinkMediaInjectPayload
        {
            Title = "T",
            PositionMs = 5_000,
            DurationMs = 60_000,
            PlaybackState = "Paused",
            PositionAgeMs = 800
        }, out _));

        Assert.Equal(TimeSpan.FromMilliseconds(5_000), store.GetMediaSnapshot()!.Position);
    }

    [Fact]
    public void PositionAgeMs_ScalesWithPlaybackRate()
    {
        var tick = 10_000L;
        var store = new MediaLinkInjectionStore(tickProvider: () => tick);

        Assert.True(store.TrySetMedia(new MediaLinkMediaInjectPayload
        {
            Title = "T",
            PositionMs = 5_000,
            DurationMs = 60_000,
            PlaybackState = "Playing",
            PlaybackRate = 2.0,
            PositionAgeMs = 500
        }, out _));

        // 2 倍速下 500ms 真实时间对应 1000ms 曲目时间
        Assert.Equal(TimeSpan.FromMilliseconds(6_000), store.GetMediaSnapshot()!.Position);
    }

    [Fact]
    public void NegativePositionAgeMs_BadRequest()
    {
        var store = new MediaLinkInjectionStore();
        Assert.False(store.TrySetMedia(new MediaLinkMediaInjectPayload
        {
            Title = "T",
            PositionMs = 0,
            DurationMs = 1_000,
            PlaybackState = "Playing",
            PositionAgeMs = -1
        }, out var err));
        Assert.Contains("positionAgeMs", err, StringComparison.OrdinalIgnoreCase);
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

    [Theory]
    [InlineData(MediaInfoChangeKind.Timeline)]
    [InlineData(MediaInfoChangeKind.Playback)]
    [InlineData(MediaInfoChangeKind.MediaProperties)]
    [InlineData(MediaInfoChangeKind.CurrentSession)]
    public void SetMedia_CarriesChangeKindToSubscribers(MediaInfoChangeKind kind)
    {
        // 种类必须随写入一起送出去。订阅方无法从存储的最终状态反推它——
        // 「换歌」与「位置前进了 20ms」在状态上长得一模一样。
        var store = new MediaLinkInjectionStore();
        var seen = new List<MediaInfoChangeKind>();
        store.Changed += (_, e) => seen.Add(e.ChangeKind);

        Assert.True(store.TrySetMedia(new MediaLinkMediaInjectPayload
        {
            Title = "A", PlaybackState = "Playing"
        }, out _, kind));

        Assert.Equal([kind], seen);
    }

    [Fact]
    public void SetMedia_WithoutChangeKind_DefaultsToCurrentSession()
    {
        // 兼容既有调用方：不传种类时行为与本参数引入前一致。
        var store = new MediaLinkInjectionStore();
        var seen = new List<MediaInfoChangeKind>();
        store.Changed += (_, e) => seen.Add(e.ChangeKind);

        Assert.True(store.TrySetMedia(new MediaLinkMediaInjectPayload
        {
            Title = "A", PlaybackState = "Playing"
        }, out _));

        Assert.Equal([MediaInfoChangeKind.CurrentSession], seen);
    }

    [Fact]
    public void RejectedSetMedia_RaisesNothing()
    {
        var store = new MediaLinkInjectionStore();
        var hits = 0;
        store.Changed += (_, _) => hits++;

        Assert.False(store.TrySetMedia(
            new MediaLinkMediaInjectPayload { Title = " " }, out _, MediaInfoChangeKind.Timeline));

        Assert.Equal(0, hits);
    }

    [Fact]
    public void LyricsAndClearAndVirtualTransport_ReportSessionLevelChange()
    {
        // 这三类写入都不是「位置前进」，必须走整条重载路径，故一律报 CurrentSession。
        var store = new MediaLinkInjectionStore();
        Assert.True(store.TrySetMedia(new MediaLinkMediaInjectPayload
        {
            Title = "A", PlaybackState = "Paused"
        }, out _, MediaInfoChangeKind.Timeline));

        var seen = new List<MediaInfoChangeKind>();
        store.Changed += (_, e) => seen.Add(e.ChangeKind);

        Assert.True(store.TryVirtualPlay(out _));
        Assert.True(store.TryVirtualPause(out _));
        Assert.True(store.TryClear(null, out _));

        Assert.Equal(
            [
                MediaInfoChangeKind.CurrentSession,
                MediaInfoChangeKind.CurrentSession,
                MediaInfoChangeKind.CurrentSession
            ],
            seen);
    }
}
