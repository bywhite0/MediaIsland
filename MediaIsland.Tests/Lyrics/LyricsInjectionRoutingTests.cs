using MediaIsland.Components;
using MediaIsland.Services.Lyrics.Models;
using Xunit;

namespace MediaIsland.Tests.Lyrics;

/// <summary>
/// 锁定「哪些歌词走注入分支」这条路由规则。
/// </summary>
/// <remarks>
/// LocalFile 与 External 都不是在线搜索结果，容易被误当作同一类处理，
/// 但两者的呈现路径必须不同：External 由 MediaLink 从另一台机器推来，
/// 本机没有播放上下文，只能原样套用；LocalFile 是用户导入并固定的歌词，
/// 经由正常查找路径产生，必须走常规流程以保持搜索版本号与播放时钟同步。
/// </remarks>
public class LyricsInjectionRoutingTests
{
    /// <summary>本地固定歌词不得走注入分支——这是本测试存在的理由。</summary>
    [Fact]
    public void LocalFile_DoesNotTriggerInjectionBranch()
    {
        Assert.False(LyricsInjectionRouting.ShouldApplyDirectly(
            isExternalMediaEffective: false,
            lyricsSource: LyricsSourceId.LocalFile));
    }

    [Fact]
    public void External_TriggersInjectionBranch()
    {
        Assert.True(LyricsInjectionRouting.ShouldApplyDirectly(
            isExternalMediaEffective: false,
            lyricsSource: LyricsSourceId.External));
    }

    [Theory]
    [InlineData(LyricsSourceId.Netease)]
    [InlineData(LyricsSourceId.QqMusic)]
    [InlineData(LyricsSourceId.Kugou)]
    [InlineData(LyricsSourceId.AmllTtml)]
    [InlineData(LyricsSourceId.SPlayerNext)]
    [InlineData(LyricsSourceId.LocalFile)]
    public void NonExternalSources_DoNotTriggerInjectionBranch(LyricsSourceId source)
    {
        Assert.False(LyricsInjectionRouting.ShouldApplyDirectly(
            isExternalMediaEffective: false,
            lyricsSource: source));
    }

    /// <summary>
    /// 媒体本身来自 MediaLink 注入时，歌词无论来自哪里都直接套用：
    /// 本机没有这首曲目的播放上下文，走搜索流程无意义。
    /// </summary>
    [Theory]
    [InlineData(LyricsSourceId.Netease)]
    [InlineData(LyricsSourceId.LocalFile)]
    [InlineData(LyricsSourceId.External)]
    public void ExternalMedia_AlwaysTriggersInjectionBranch(LyricsSourceId source)
    {
        Assert.True(LyricsInjectionRouting.ShouldApplyDirectly(
            isExternalMediaEffective: true,
            lyricsSource: source));
    }

    [Fact]
    public void NullLyrics_WithLocalMedia_DoesNotTriggerInjectionBranch()
    {
        Assert.False(LyricsInjectionRouting.ShouldApplyDirectly(
            isExternalMediaEffective: false,
            lyricsSource: null));
    }

    /// <summary>歌词为 null 但媒体来自注入时仍走注入分支——用于清空上一首的歌词。</summary>
    [Fact]
    public void NullLyrics_WithExternalMedia_TriggersInjectionBranch()
    {
        Assert.True(LyricsInjectionRouting.ShouldApplyDirectly(
            isExternalMediaEffective: true,
            lyricsSource: null));
    }
}
