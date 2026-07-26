using MediaIsland.Services.Lyrics.Parsers;
using Xunit;

namespace MediaIsland.Tests.Lyrics;

public class LrcLyricsParserTests
{
    [Fact]
    public void Parse_StandardTimestamps_ReturnsLinesInOrder()
    {
        const string content = """
            [00:12.34]first line
            [00:15.00]second line
            """;

        var lines = LrcLyricsParser.Parse(content);

        Assert.Collection(
            lines,
            line =>
            {
                Assert.Equal(TimeSpan.FromMilliseconds(12340), line.StartTime);
                Assert.Equal("first line", line.Text);
                Assert.Empty(line.Words);
            },
            line =>
            {
                Assert.Equal(TimeSpan.FromSeconds(15), line.StartTime);
                Assert.Equal("second line", line.Text);
            });
    }

    [Fact]
    public void Parse_MultipleTimestampsOnOneLine_RepeatsTextForEach()
    {
        const string content = "[00:01.00][00:05.50]shared text";

        var lines = LrcLyricsParser.Parse(content);

        Assert.Equal(2, lines.Count);
        Assert.All(lines, line => Assert.Equal("shared text", line.Text));
        Assert.Equal(TimeSpan.FromSeconds(1), lines[0].StartTime);
        Assert.Equal(TimeSpan.FromMilliseconds(5500), lines[1].StartTime);
    }

    [Theory]
    [InlineData("[00:01.5]x", 1500)]
    [InlineData("[00:01.50]x", 1500)]
    [InlineData("[00:01.500]x", 1500)]
    [InlineData("[00:01.05]x", 1050)]
    [InlineData("[00:01.005]x", 1005)]
    [InlineData("[00:01]x", 1000)]
    [InlineData("[00:12:34]x", 12340)]
    [InlineData("[01:02.00]x", 62000)]
    public void Parse_TimestampVariants_ResolveToMilliseconds(string content, int expectedMilliseconds)
    {
        var line = Assert.Single(LrcLyricsParser.Parse(content));

        Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), line.StartTime);
    }

    [Fact]
    public void Parse_PositiveOffset_MovesFollowingLinesEarlier()
    {
        const string content = """
            [offset:500]
            [00:10.00]shifted
            """;

        var line = Assert.Single(LrcLyricsParser.Parse(content));

        Assert.Equal(TimeSpan.FromMilliseconds(9500), line.StartTime);
    }

    [Fact]
    public void Parse_NegativeOffset_MovesFollowingLinesLater()
    {
        const string content = """
            [offset:-500]
            [00:10.00]shifted
            """;

        var line = Assert.Single(LrcLyricsParser.Parse(content));

        Assert.Equal(TimeSpan.FromMilliseconds(10500), line.StartTime);
    }

    [Fact]
    public void Parse_OffsetDeclaredMidway_OnlyAffectsLaterLines()
    {
        const string content = """
            [00:10.00]before
            [offset:300]
            [00:20.00]after
            """;

        var lines = LrcLyricsParser.Parse(content);

        Assert.Equal(TimeSpan.FromSeconds(10), lines[0].StartTime);
        Assert.Equal(TimeSpan.FromMilliseconds(19700), lines[1].StartTime);
    }

    [Fact]
    public void Parse_MetadataTags_AreIgnored()
    {
        const string content = """
            [ar:Artist]
            [ti:Title]
            [al:Album]
            [00:01.00]after metadata
            """;

        var line = Assert.Single(LrcLyricsParser.Parse(content));

        Assert.Equal("after metadata", line.Text);
    }

    [Fact]
    public void Parse_TagAfterTimestamp_BelongsToLyricText()
    {
        const string content = "[00:01.00][ti:Title]actual text";

        var line = Assert.Single(LrcLyricsParser.Parse(content));

        Assert.Equal("[ti:Title]actual text", line.Text);
    }

    [Fact]
    public void Parse_UnorderedInput_IsSortedByStartTime()
    {
        const string content = """
            [00:09.00]late
            [00:01.00]early
            """;

        var lines = LrcLyricsParser.Parse(content);

        Assert.Equal("early", lines[0].Text);
        Assert.Equal("late", lines[1].Text);
    }

    [Fact]
    public void Parse_TimestampWithoutText_KeepsEmptyLine()
    {
        const string content = """
            [00:01.00]
            [00:02.00]has text
            """;

        var lines = LrcLyricsParser.Parse(content);

        Assert.Equal(2, lines.Count);
        Assert.Equal(string.Empty, lines[0].Text);
    }

    [Fact]
    public void Parse_SurroundingWhitespace_IsTrimmed()
    {
        const string content = "[00:01.00]   padded   ";

        var line = Assert.Single(LrcLyricsParser.Parse(content));

        Assert.Equal("padded", line.Text);
    }

    [Theory]
    [InlineData("plain text without any tag")]
    [InlineData("[00:aa.bb]bad timestamp")]
    [InlineData("[00:01.00 unterminated tag")]
    [InlineData("[ar:Artist]")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Parse_ContentWithoutUsableTimestamps_ReturnsEmpty(string? content)
    {
        Assert.Empty(LrcLyricsParser.Parse(content));
    }

    [Fact]
    public void Parse_CrlfAndBlankLines_AreHandled()
    {
        const string content = "[00:01.00]a\r\n\r\n[00:02.00]b\r\n";

        var lines = LrcLyricsParser.Parse(content);

        Assert.Equal(2, lines.Count);
        Assert.Equal("a", lines[0].Text);
        Assert.Equal("b", lines[1].Text);
    }
}
