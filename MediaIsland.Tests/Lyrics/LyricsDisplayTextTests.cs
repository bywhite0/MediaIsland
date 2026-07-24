using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Lyrics.Models;
using Xunit;

namespace MediaIsland.Tests.Lyrics;

public class LyricsDisplayTextTests
{
    private static LyricsLine CreateLine(
        string text,
        string? translation = null,
        string? romanization = null) =>
        new(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            text,
            [],
            translation,
            romanization);

    [Fact]
    public void Resolve_UsesOriginalByDefault()
    {
        var line = CreateLine("原文", "翻译", "yin yi");
        Assert.Equal("原文", LyricsDisplayText.Resolve(line, LyricsDisplayPart.Original));
    }

    [Fact]
    public void Resolve_PrefersTranslationWhenAvailable()
    {
        var line = CreateLine("原文", "翻译", "yin yi");
        Assert.Equal("翻译", LyricsDisplayText.Resolve(line, LyricsDisplayPart.Translation));
    }

    [Fact]
    public void Resolve_FallsBackToOriginalWhenTranslationMissing()
    {
        var line = CreateLine("原文", null, "yin yi");
        Assert.Equal("原文", LyricsDisplayText.Resolve(line, LyricsDisplayPart.Translation));
        Assert.True(LyricsDisplayText.UsesOriginalText(line, LyricsDisplayPart.Translation));
    }

    [Fact]
    public void Resolve_PrefersRomanizationWhenAvailable()
    {
        var line = CreateLine("原文", "翻译", "yin yi");
        Assert.Equal("yin yi", LyricsDisplayText.Resolve(line, LyricsDisplayPart.Romanization));
        Assert.False(LyricsDisplayText.UsesOriginalText(line, LyricsDisplayPart.Romanization));
    }

    [Fact]
    public void Resolve_FallsBackToOriginalWhenRomanizationMissing()
    {
        var line = CreateLine("原文", "翻译", "  ");
        Assert.Equal("原文", LyricsDisplayText.Resolve(line, LyricsDisplayPart.Romanization));
        Assert.True(LyricsDisplayText.UsesOriginalText(line, LyricsDisplayPart.Romanization));
    }
}
