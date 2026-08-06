using MediaIsland.Services.Lyrics;
using Xunit;

namespace MediaIsland.Tests.Lyrics;

public class LyricsTrackKeyTests
{
    [Fact]
    public void Compute_IsStable_ForIdenticalInput()
    {
        Assert.Equal(
            LyricsTrackKey.Compute("Lemon", "米津玄師", "Lemon"),
            LyricsTrackKey.Compute("Lemon", "米津玄師", "Lemon"));
    }

    [Fact]
    public void Compute_ReturnsSixteenLowercaseHexChars()
    {
        var key = LyricsTrackKey.Compute("Lemon", "米津玄師", "Lemon");

        Assert.Equal(16, key.Length);
        Assert.All(key, c => Assert.Contains(c, "0123456789abcdef"));
    }

    [Theory]
    [InlineData("LEMON", "米津玄師", "Lemon")]
    [InlineData("  Lemon  ", "米津玄師", "Lemon")]
    [InlineData("Lemon", "米津玄師", "Lemon")]
    [InlineData("Le-mon", "米津玄師", "Lemon")]
    [InlineData("Le_mon", "米津玄師", "Lemon")]
    [InlineData("(Lemon)", "米津玄師", "Lemon")]
    public void Compute_IgnoresCaseWhitespaceAndSeparators(string title, string artist, string album)
    {
        Assert.Equal(
            LyricsTrackKey.Compute("Lemon", "米津玄師", "Lemon"),
            LyricsTrackKey.Compute(title, artist, album));
    }

    [Fact]
    public void Compute_DiffersByAlbum()
    {
        Assert.NotEqual(
            LyricsTrackKey.Compute("Lemon", "米津玄師", "Lemon"),
            LyricsTrackKey.Compute("Lemon", "米津玄師", "BOOTLEG"));
    }

    [Fact]
    public void Compute_DiffersByArtist()
    {
        Assert.NotEqual(
            LyricsTrackKey.Compute("Lemon", "米津玄師", "Lemon"),
            LyricsTrackKey.Compute("Lemon", "Someone Else", "Lemon"));
    }

    /// <summary>时长不参与键计算——这是跨播放器共享缓存的核心保证。</summary>
    [Fact]
    public void Compute_HasNoDurationParameter()
    {
        var method = typeof(LyricsTrackKey).GetMethod(nameof(LyricsTrackKey.Compute));

        Assert.NotNull(method);
        Assert.Equal(3, method!.GetParameters().Length);
        Assert.All(method.GetParameters(), p => Assert.Equal(typeof(string), p.ParameterType));
    }

    [Fact]
    public void Compute_TreatsNullAndEmptyAlike()
    {
        Assert.Equal(
            LyricsTrackKey.Compute("Lemon", null, null),
            LyricsTrackKey.Compute("Lemon", "", ""));
    }

    /// <summary>分隔符必须防止字段拼接产生歧义。</summary>
    [Fact]
    public void Compute_DoesNotCollideAcrossFieldBoundaries()
    {
        Assert.NotEqual(
            LyricsTrackKey.Compute("AB", "C", ""),
            LyricsTrackKey.Compute("A", "BC", ""));
    }

    /// <summary>
    /// 非半角空白（制表符、NBSP、em space）同样不参与键计算：
    /// 它们不携带曲目身份信息，若保留会造成假性未命中。
    /// 用显式 Unicode 转义而非不可见字面量，确保测试真正命中这些字符。
    /// </summary>
    [Theory]
    [InlineData("Two\tWords")]
    [InlineData("Two Words")]
    [InlineData("Two Words")]
    public void Compute_IgnoresNonBreakingAndExoticWhitespace(string title)
    {
        Assert.Equal(
            LyricsTrackKey.Compute("TwoWords", "米津玄師", "Lemon"),
            LyricsTrackKey.Compute(title, "米津玄師", "Lemon"));
    }
}
