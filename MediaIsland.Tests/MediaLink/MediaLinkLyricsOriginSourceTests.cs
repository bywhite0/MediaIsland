using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Lyrics.Models;
using MediaIsland.Services.MediaLink.Mapping;
using MediaIsland.Services.MediaLink.Protocol;
using Xunit;

namespace MediaIsland.Tests.MediaLink;

/// <summary>
/// 注入歌词的来源标注。
///
/// 关键约束：Source 恒为 External 是**路由要求**而非标签——LyricsComponent 按
/// Source == External 决定是否直接应用注入歌词。真实来源另存于 OriginSource，
/// 只用于显示。
/// </summary>
public class MediaLinkLyricsOriginSourceTests
{
    [Theory]
    [InlineData("QqMusic", LyricsSourceId.QqMusic)]
    [InlineData("Netease", LyricsSourceId.Netease)]
    [InlineData("Kugou", LyricsSourceId.Kugou)]
    [InlineData("AmllTtml", LyricsSourceId.AmllTtml)]
    [InlineData("SPlayerNext", LyricsSourceId.SPlayerNext)]
    public void InjectedLyrics_PreservesUpstreamSourceAsOrigin(string wire, LyricsSourceId expected)
    {
        var payload = NewPayload(documentSource: wire);

        Assert.True(MediaLinkDtoMapper.TryMapInjectedLyrics(payload, out var result, out _));
        Assert.Equal(expected, result!.OriginSource);
    }

    [Fact]
    public void InjectedLyrics_ChannelStaysExternal_RegardlessOfUpstreamSource()
    {
        // 这条是防回归的核心：把 Source 改成上游真实来源会让注入歌词不再显示。
        var payload = NewPayload(documentSource: "QqMusic");

        Assert.True(MediaLinkDtoMapper.TryMapInjectedLyrics(payload, out var result, out _));
        Assert.Equal(LyricsSourceId.External, result!.Source);
        Assert.Equal(LyricsSourceId.External, result.Document.Source);
    }

    [Fact]
    public void InjectedLyrics_FallsBackToTopLevelSource()
    {
        // document.source 缺失时退回信封上的 source。
        var payload = NewPayload(documentSource: null, topLevelSource: "Kugou");

        Assert.True(MediaLinkDtoMapper.TryMapInjectedLyrics(payload, out var result, out _));
        Assert.Equal(LyricsSourceId.Kugou, result!.OriginSource);
    }

    [Fact]
    public void InjectedLyrics_UnknownSource_YieldsNullOrigin()
    {
        // 协议要求容忍未知枚举值；上游新增来源时按"来源不详"处理，不抛异常。
        var payload = NewPayload(documentSource: "SomeFutureProvider");

        Assert.True(MediaLinkDtoMapper.TryMapInjectedLyrics(payload, out var result, out _));
        Assert.Null(result!.OriginSource);
        Assert.Equal(LyricsSourceId.External, result.Source);
    }

    [Fact]
    public void InjectedLyrics_NoSourceAtAll_YieldsNullOrigin()
    {
        var payload = NewPayload(documentSource: null, topLevelSource: null);

        Assert.True(MediaLinkDtoMapper.TryMapInjectedLyrics(payload, out var result, out _));
        Assert.Null(result!.OriginSource);
    }

    [Fact]
    public void OutboundDto_ReportsOriginSource_SoRelayHopsDoNotDegrade()
    {
        // 转发链：上游 QQ 音乐 → 本机（通道 External，来源 QqMusic）→ 下游。
        // 若对外仍报 External，第二跳就再也看不出真实来源。
        var payload = NewPayload(documentSource: "QqMusic");
        Assert.True(MediaLinkDtoMapper.TryMapInjectedLyrics(payload, out var injected, out _));

        var dto = MediaLinkDtoMapper.ToLyricsDto(injected);

        Assert.Equal("QqMusic", dto!.Source);
    }

    [Fact]
    public void OutboundDto_LocalSearchResult_ReportsItsOwnSource()
    {
        // 本机搜索到的歌词没有 OriginSource，应照常报自身来源。
        var document = new LyricsDocument(
            new LyricsMetadata("T", "A", null, null),
            [],
            LyricsSyncMode.Line,
            LyricsSourceId.Netease,
            "item",
            LyricsFormat.Lrc);
        var result = new LyricsSearchResult(
            document, "item", "T", "A", TimeSpan.FromSeconds(10), 50, LyricsSourceId.Netease);

        var dto = MediaLinkDtoMapper.ToLyricsDto(result);

        Assert.Equal("Netease", dto!.Source);
    }

    [Theory]
    [InlineData("qqmusic")]
    [InlineData("QQMUSIC")]
    public void ParseLyricsSource_IsCaseInsensitive(string wire)
    {
        Assert.Equal(LyricsSourceId.QqMusic, MediaLinkDtoMapper.ParseLyricsSource(wire));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("Unknown")]
    public void ParseLyricsSource_UnusableInput_ReturnsNull(string? wire)
    {
        Assert.Null(MediaLinkDtoMapper.ParseLyricsSource(wire));
    }

    private static MediaLinkLyricsDto NewPayload(string? documentSource, string? topLevelSource = null) => new()
    {
        Id = "ly-1",
        Title = "Song",
        Artist = "Artist",
        DurationMs = 120_000,
        Source = topLevelSource ?? string.Empty,
        Document = new MediaLinkLyricsDocumentDto
        {
            Format = "Lrc",
            SyncMode = "Line",
            ProviderItemId = "item-1",
            Source = documentSource ?? string.Empty,
            Lines =
            [
                new MediaLinkLyricsLineDto { StartMs = 0, EndMs = 5_000, Text = "line" }
            ]
        }
    };
}
