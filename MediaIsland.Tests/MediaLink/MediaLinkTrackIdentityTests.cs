using MediaIsland.Services.MediaLink;
using MediaIsland.Services.MediaLink.Mapping;
using MediaIsland.Services.MediaLink.Protocol;
using Xunit;

namespace MediaIsland.Tests.MediaLink;

/// <summary>
/// 曲目标识的规范化。它存在的唯一理由是让 token 在链路两端相等——
/// 发送端算 token 用的是本机原始字段，接收端算 token 用的是注入存储里已规范化的字段，
/// 两者不走同一个入口时，带空白的元数据会让接收端把全部音频帧判为过期曲目而静默丢光。
/// </summary>
public class MediaLinkTrackIdentityTests
{
    [Fact]
    public void Normalize_TrimsSurroundingWhitespace()
    {
        var identity = MediaLinkTrackIdentity.Normalize("  Spotify.exe ", " 標題 ", "\tArtist\n", " Album ");

        Assert.Equal("Spotify.exe", identity.SourceApp);
        Assert.Equal("標題", identity.Title);
        Assert.Equal("Artist", identity.Artist);
        Assert.Equal("Album", identity.AlbumTitle);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_BlankSourceApp_FallsBackToExternal(string? raw)
    {
        // 与注入存储的既有规则一致：来源缺失时统一记为 external，
        // 否则同一台上游会因为偶发的空 sourceApp 算出两个 token。
        Assert.Equal("external", MediaLinkTrackIdentity.Normalize(raw, "T", null, null).SourceApp);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \t ")]
    public void Normalize_BlankOptionalFields_BecomeNull(string? raw)
    {
        var identity = MediaLinkTrackIdentity.Normalize("app", raw, raw, raw);

        Assert.Null(identity.Title);
        Assert.Null(identity.Artist);
        Assert.Null(identity.AlbumTitle);
    }

    [Fact]
    public void Normalize_IsIdempotent()
    {
        // 幂等是中继链的前提：A 规范化一次、B 存下来再规范化一次、C 又一次，
        // 不幂等则每跳都可能再变一次值，链路越长越容易失配。
        var once = MediaLinkTrackIdentity.Normalize(" app ", " title ", " artist ", " album ");
        var twice = MediaLinkTrackIdentity.Normalize(
            once.SourceApp, once.Title, once.Artist, once.AlbumTitle);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void Normalize_PreservesInnerWhitespace()
    {
        // 只削首尾。曲名里的空格是内容，削掉会把两首不同的歌算成同一个 token。
        Assert.Equal("A  B", MediaLinkTrackIdentity.Normalize("app", " A  B ", null, null).Title);
    }

    [Fact]
    public void ComputeTrackToken_IsStableAcrossNormalization()
    {
        // 本条锁住整个改动的目的：发送端拿原始字段算，接收端拿规范化后的字段算，
        // 两者必须相等。不相等时接收端会丢光全部音频帧。
        var raw = MediaLinkDtoMapper.ComputeTrackToken(" Spotify.exe ", " 標題 ", " 歌手 ", " 專輯 ");
        var normalized = MediaLinkDtoMapper.ComputeTrackToken("Spotify.exe", "標題", "歌手", "專輯");

        Assert.Equal(raw, normalized);
    }

    [Fact]
    public void ComputeTrackToken_BlankAndNullOptionalFields_Agree()
    {
        Assert.Equal(
            MediaLinkDtoMapper.ComputeTrackToken("app", "title", null, null),
            MediaLinkDtoMapper.ComputeTrackToken("app", "title", "   ", string.Empty));
    }

    [Fact]
    public void ComputeTrackToken_DistinctTracks_StillDiffer()
    {
        // 地基：规范化不能规范到把不同曲目折叠成同一个 token。
        Assert.NotEqual(
            MediaLinkDtoMapper.ComputeTrackToken("app", "A", null, null),
            MediaLinkDtoMapper.ComputeTrackToken("app", "B", null, null));
    }

    [Fact]
    public void InjectedMedia_StoresNormalizedFields()
    {
        // 注入存储与 ComputeTrackToken 共用规范化入口后，存下来的必须已是规范化的值。
        // 存原始值只会在别处露馅：快照被拿去显示、比对、再转发，带空白的字段每经一处都要重削一次。
        var store = new MediaLinkInjectionStore();

        Assert.True(store.TrySetMedia(new MediaLinkMediaInjectPayload
        {
            SourceApp = " Spotify.exe ",
            Title = " 標題 ",
            Artist = "\t歌手\n",
            AlbumTitle = " 專輯 ",
            DurationMs = 1000,
            PlaybackState = "Playing"
        }, out var error), error);

        var snapshot = store.GetMediaSnapshot();
        Assert.NotNull(snapshot);
        Assert.Equal("Spotify.exe", snapshot.SourceApp);
        Assert.Equal("標題", snapshot.Title);
        Assert.Equal("歌手", snapshot.Artist);
        Assert.Equal("專輯", snapshot.AlbumTitle);
    }

    [Fact]
    public void InjectedMedia_BlankOptionalFields_StoreNullAndExternalSource()
    {
        var store = new MediaLinkInjectionStore();

        Assert.True(store.TrySetMedia(new MediaLinkMediaInjectPayload
        {
            SourceApp = "   ",
            Title = " T ",
            Artist = "  ",
            AlbumTitle = string.Empty,
            DurationMs = 1000,
            PlaybackState = "Playing"
        }, out var error), error);

        var snapshot = store.GetMediaSnapshot();
        Assert.NotNull(snapshot);
        Assert.Equal("external", snapshot.SourceApp);
        Assert.Null(snapshot.Artist);
        Assert.Null(snapshot.AlbumTitle);
    }
}
