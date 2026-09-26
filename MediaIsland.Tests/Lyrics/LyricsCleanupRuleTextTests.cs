using MediaIsland.Services.Lyrics.Cleanup;
using Xunit;

namespace MediaIsland.Tests.Lyrics;

public class LyricsCleanupRuleTextTests
{
    [Fact]
    public void ParseLines_SplitsOnAnyNewline_TrimsDropsBlankAndDeduplicates()
    {
        Assert.Equal(["监修", "出品"], LyricsCleanupRuleText.ParseLines(" 监修 \r\n\n  \r\n出品\n监修"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\r\n \n")]
    public void ParseLines_EmptyInput_ReturnsEmpty(string? text)
    {
        Assert.Empty(LyricsCleanupRuleText.ParseLines(text));
    }

    [Fact]
    public void Format_JoinsWithNewline_AndRoundTrips()
    {
        var text = LyricsCleanupRuleText.Format(["a", "b"]);

        Assert.Equal($"a{Environment.NewLine}b", text);
        Assert.Equal(["a", "b"], LyricsCleanupRuleText.ParseLines(text));
    }

    [Fact]
    public void PartitionRegexes_SeparatesInvalidAndEmptyMatching()
    {
        var (valid, invalid) = LyricsCleanupRuleText.PartitionRegexes(["^OP", "(", ".*", "b[a]d"]);

        Assert.Equal(["^OP", "b[a]d"], valid);
        Assert.Equal(["(", ".*"], invalid);
    }
}
