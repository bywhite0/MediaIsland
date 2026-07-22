using Xunit;
using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Media;

namespace MediaIsland.Tests.Lyrics;

public class LyricsCandidateScorerTests
{
    [Fact]
    public void Score_PrefersExactMetadataAndDuration()
    {
        var media = new MediaInfo(
            "Spotify.exe",
            "ME!",
            "Taylor Swift",
            "Reputation",
            TimeSpan.Zero,
            TimeSpan.FromSeconds(200),
            new MediaPlaybackInfo(MediaPlaybackState.Playing),
            null,
            null);

        var good = LyricsCandidateScorer.Score(media, "ME!", "Taylor Swift", "Reputation", TimeSpan.FromSeconds(201));
        var bad = LyricsCandidateScorer.Score(media, "Blank Space", "Taylor Swift", "1989", TimeSpan.FromSeconds(231));
        Assert.True(good > bad);
        Assert.True(good >= LyricsCandidateScorer.MinimumScore(media));
    }

    [Fact]
    public void Score_KeepsExactSpotifyMetadataAcceptable_WhenPlatformDurationDiffers()
    {
        var media = new MediaInfo(
            "Spotify.exe",
            "Song Title",
            "Artist A",
            "Spotify Album Label",
            TimeSpan.Zero,
            TimeSpan.FromSeconds(240),
            new MediaPlaybackInfo(MediaPlaybackState.Playing),
            null,
            null);

        var score = LyricsCandidateScorer.Score(
            media,
            "Song Title",
            "Artist A",
            "Original Album",
            TimeSpan.FromSeconds(180));

        Assert.True(score >= LyricsCandidateScorer.MinimumScore(media));
    }
}
