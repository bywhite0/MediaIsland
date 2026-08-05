using MediaIsland.Helpers;
using Xunit;

namespace MediaIsland.Tests.Media;

public class MediaSourceProcessMatcherTests
{
    [Fact]
    public void GetIdentifierVariants_ReverseDomainAumid_SplitsIntoUsableSegments()
    {
        var variants = MediaSourceProcessMatcher.GetIdentifierVariants("cn.toside.music.desktop");

        Assert.Contains("toside", variants);
        Assert.Contains("music", variants);
        Assert.Contains("desktop", variants);
    }

    [Fact]
    public void GetIdentifierVariants_DropsSegmentsTooShortToBeDistinctive()
    {
        var variants = MediaSourceProcessMatcher.GetIdentifierVariants("cn.toside.music.desktop");

        Assert.DoesNotContain("cn", variants);
    }

    [Fact]
    public void GetIdentifierVariants_StripsNoiseSegments()
    {
        var variants = MediaSourceProcessMatcher.GetIdentifierVariants("com.github.player.exe");

        Assert.DoesNotContain("com", variants);
        Assert.DoesNotContain("github", variants);
        Assert.DoesNotContain("exe", variants);
        Assert.Contains("player", variants);
    }

    [Fact]
    public void GetIdentifierVariants_OrdersLongestFirst()
    {
        var variants = MediaSourceProcessMatcher.GetIdentifierVariants("cn.toside.music.desktop");

        var lengths = variants.Select(variant => variant.Length).ToList();
        Assert.Equal(lengths.OrderByDescending(length => length), lengths);
    }

    [Fact]
    public void GetIdentifierVariants_AlwaysKeepsOriginalAumidAsFallback()
    {
        var variants = MediaSourceProcessMatcher.GetIdentifierVariants("SomeOpaqueId");

        Assert.Contains("SomeOpaqueId", variants);
    }

    [Fact]
    public void GetIdentifierVariants_SplitsPackagedAumidOnBang()
    {
        var variants = MediaSourceProcessMatcher.GetIdentifierVariants(
            "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify");

        Assert.Contains("Spotify", variants);
        Assert.Contains("SpotifyMusic", variants);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a")]
    public void GetIdentifierVariants_EmptyOrTooShortInput_ReturnsNoVariants(string sourceApp)
    {
        Assert.Empty(MediaSourceProcessMatcher.GetIdentifierVariants(sourceApp));
    }

    [Fact]
    public void ScoreCandidate_LxMusicProcess_MatchesReverseDomainAumid()
    {
        const string sourceApp = "cn.toside.music.desktop";
        var variants = MediaSourceProcessMatcher.GetIdentifierVariants(sourceApp);

        var score = MediaSourceProcessMatcher.ScoreCandidate(
            sourceApp,
            @"C:\Program Files\lx-music-desktop\lx-music-desktop.exe",
            hasMainWindow: true,
            variants);

        Assert.True(score > 0);
    }

    [Fact]
    public void ScoreCandidate_UnrelatedProcess_DoesNotMatch()
    {
        const string sourceApp = "cn.toside.music.desktop";
        var variants = MediaSourceProcessMatcher.GetIdentifierVariants(sourceApp);

        var score = MediaSourceProcessMatcher.ScoreCandidate(
            sourceApp,
            @"C:\Windows\System32\notepad.exe",
            hasMainWindow: true,
            variants);

        Assert.Equal(0, score);
    }

    [Fact]
    public void ScoreCandidate_ExactExecutableName_OutranksPathOnlyMatch()
    {
        const string sourceApp = "cloudmusic.exe";
        var variants = MediaSourceProcessMatcher.GetIdentifierVariants(sourceApp);

        var mainScore = MediaSourceProcessMatcher.ScoreCandidate(
            sourceApp,
            @"C:\Program Files\cloudmusic\cloudmusic.exe",
            hasMainWindow: false,
            variants);
        var helperScore = MediaSourceProcessMatcher.ScoreCandidate(
            sourceApp,
            @"C:\Program Files\cloudmusic\cloudmusic_helper.exe",
            hasMainWindow: true,
            variants);

        Assert.True(mainScore > helperScore);
    }

    [Fact]
    public void ScoreCandidate_MainWindowProcess_OutranksBackgroundProcess()
    {
        const string sourceApp = "net.stevexmh.amllplayer";
        var variants = MediaSourceProcessMatcher.GetIdentifierVariants(sourceApp);
        const string path = @"C:\Apps\amllplayer\amllplayer.exe";

        var windowed = MediaSourceProcessMatcher.ScoreCandidate(sourceApp, path, true, variants);
        var background = MediaSourceProcessMatcher.ScoreCandidate(sourceApp, path, false, variants);

        Assert.True(windowed > background);
    }

    [Fact]
    public void ScoreCandidate_MatchIsCaseInsensitive()
    {
        const string sourceApp = "cloudmusic.exe";
        var variants = MediaSourceProcessMatcher.GetIdentifierVariants(sourceApp);

        var score = MediaSourceProcessMatcher.ScoreCandidate(
            sourceApp,
            @"C:\Program Files\CloudMusic\CloudMusic.exe",
            hasMainWindow: true,
            variants);

        Assert.True(score > 0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ScoreCandidate_EmptyPath_ReturnsZero(string processPath)
    {
        var variants = MediaSourceProcessMatcher.GetIdentifierVariants("cloudmusic.exe");

        Assert.Equal(0, MediaSourceProcessMatcher.ScoreCandidate(
            "cloudmusic.exe",
            processPath,
            hasMainWindow: true,
            variants));
    }

    [Fact]
    public void ScoreCandidate_NoVariants_ReturnsZero()
    {
        Assert.Equal(0, MediaSourceProcessMatcher.ScoreCandidate(
            "a",
            @"C:\Windows\System32\notepad.exe",
            hasMainWindow: true,
            []));
    }
}
